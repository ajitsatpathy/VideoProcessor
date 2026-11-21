using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace VideoProcessorApp
{
    public class WebServer
    {
        private readonly HttpListener _listener;
        private readonly int _port;
        private readonly string _tempFolder;
        private readonly Processor _processor;
        private bool _isRunning;

        public class VideoTask
        {
            public string Id { get; set; }
            public string Status { get; set; } // "processing", "completed", "failed"
            public string OutputFilePath { get; set; }
            public string ErrorMessage { get; set; }
            public DateTime CreatedAt { get; set; }

            public VideoTask()
            {
                CreatedAt = DateTime.Now;
            }
        }

        private static readonly ConcurrentDictionary<string, VideoTask> Tasks = new ConcurrentDictionary<string, VideoTask>();

        public WebServer(int port)
        {
            _port = port;
            _listener = new HttpListener();
            _listener.Prefixes.Add(string.Format("http://*:{0}/", _port));
            _tempFolder = Path.Combine(Path.GetTempPath(), "VideoProcessor");
            _processor = new Processor();

            if (!Directory.Exists(_tempFolder))
            {
                Directory.CreateDirectory(_tempFolder);
            }
        }

        public void Start()
        {
            _listener.Start();
            _isRunning = true;
            Console.WriteLine(string.Format("[INFO] Web server started on port {0}. Listening on http://localhost:{0}/", _port));
            
            // Clean up old temp files on startup
            CleanTempDirectory();

            Task.Run(async () =>
            {
                while (_isRunning)
                {
                    try
                    {
                        var context = await _listener.GetContextAsync();
                        Task.Run(() => HandleRequest(context)); // Handle requests concurrently
                    }
                    catch (HttpListenerException)
                    {
                        // Expected when listener is stopped
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[ERROR] Error in request listener: " + ex.Message);
                    }
                }
            });
        }

        public void Stop()
        {
            _isRunning = false;
            _listener.Stop();
            _listener.Close();
            Console.WriteLine("[INFO] Web server stopped.");
        }

        private async Task HandleRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            // Set default headers
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = (int)HttpStatusCode.OK;
                response.Close();
                return;
            }

            try
            {
                string rawUrl = request.RawUrl ?? "/";
                string path = rawUrl.Split('?')[0];

                Console.WriteLine(string.Format("[INFO] Received {0} request for {1}", request.HttpMethod, path));

                if (request.HttpMethod == "GET" && path == "/")
                {
                    ServeFrontend(response);
                }
                else if (request.HttpMethod == "POST" && path == "/api/upload")
                {
                    await HandleUpload(request, response);
                }
                else if (request.HttpMethod == "GET" && path == "/api/status")
                {
                    HandleStatus(request, response);
                }
                else if (request.HttpMethod == "GET" && path == "/api/download")
                {
                    HandleDownload(request, response);
                }
                else
                {
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    SendString(response, "Not Found", "text/plain");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ERROR] Request handler threw exception: " + ex);
                try
                {
                    response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    SendJson(response, string.Format("{{\"error\": \"Internal server error: {0}\"}}", ex.Message));
                }
                catch { }
            }
        }

        private void ServeFrontend(HttpListenerResponse response)
        {
            string html = GetEmbeddedFrontendHtml();
            SendString(response, html, "text/html; charset=utf-8");
        }

        private async Task HandleUpload(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (!request.HasEntityBody)
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                SendJson(response, "{\"error\": \"No files uploaded\"}");
                return;
            }

            if (!_processor.IsFFmpegAvailable())
            {
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                SendJson(response, "{\"error\": \"FFmpeg engine is not available on this server. Please download ffmpeg.exe and run again.\"}");
                return;
            }

            string contentType = request.ContentType;
            if (string.IsNullOrEmpty(contentType) || !contentType.StartsWith("multipart/form-data"))
            {
                response.StatusCode = (int)HttpStatusCode.UnsupportedMediaType;
                SendJson(response, "{\"error\": \"Request must be multipart/form-data\"}");
                return;
            }

            // Extract boundary
            string boundary = null;
            var parts = contentType.Split(';');
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase))
                {
                    boundary = trimmed.Substring("boundary=".Length);
                    if (boundary.StartsWith("\"") && boundary.EndsWith("\""))
                    {
                        boundary = boundary.Substring(1, boundary.Length - 2);
                    }
                    break;
                }
            }

            if (string.IsNullOrEmpty(boundary))
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                SendJson(response, "{\"error\": \"Multipart boundary is missing\"}");
                return;
            }

            // Create a task-specific temp sub-directory
            string taskId = Guid.NewGuid().ToString("N");
            string taskFolder = Path.Combine(_tempFolder, taskId);
            Directory.CreateDirectory(taskFolder);

            // Parse request stream asynchronously
            MultipartParser parser;
            try
            {
                parser = new MultipartParser(request.InputStream, boundary, taskFolder);
            }
            catch (Exception ex)
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                SendJson(response, string.Format("{{\"error\": \"Failed to initialize multipart parser: {0}\"}}", ex.Message));
                return;
            }

            // Parse the form content
            MultipartParser.ParseResult parseResult;
            try
            {
                parseResult = await Task.Run(() => parser.Parse());
            }
            catch (Exception ex)
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                SendJson(response, string.Format("{{\"error\": \"Failed to parse file uploads: {0}\"}}", ex.Message));
                return;
            }

            // Extract values
            string speedVal;
            parseResult.Fields.TryGetValue("speed", out speedVal);
            double speed;
            if (!double.TryParse(speedVal, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out speed))
            {
                speed = 2.0; // Default fallback
            }

            // Sort the uploaded videos by numerical index to maintain exact sequence
            var videoPartList = new List<MultipartParser.FilePart>();
            string bgMusicFile = null;

            foreach (var file in parseResult.Files)
            {
                if (file.Name.StartsWith("video_"))
                {
                    videoPartList.Add(file);
                }
                else if (file.Name == "bgMusic")
                {
                    bgMusicFile = file.TempFilePath;
                }
            }

            videoPartList.Sort((a, b) =>
            {
                int idxA, idxB;
                string suffixA = a.Name.Substring("video_".Length);
                string suffixB = b.Name.Substring("video_".Length);
                if (int.TryParse(suffixA, out idxA) && int.TryParse(suffixB, out idxB))
                {
                    return idxA.CompareTo(idxB);
                }
                return string.Compare(a.Name, b.Name, StringComparison.Ordinal);
            });

            var videoFiles = new List<string>();
            foreach (var file in videoPartList)
            {
                videoFiles.Add(file.TempFilePath);
            }

            if (videoFiles.Count == 0)
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                SendJson(response, "{\"error\": \"No videos uploaded. At least one video file is required.\"}");
                return;
            }

            if (string.IsNullOrEmpty(bgMusicFile))
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                SendJson(response, "{\"error\": \"Background music file is required.\"}");
                return;
            }

            // Initialize the background task
            var videoTask = new VideoTask
            {
                Id = taskId,
                Status = "processing"
            };
            Tasks[taskId] = videoTask;

            // Trigger background processing so we can return response immediately
            Task.Run(() =>
            {
                try
                {
                    string outputDir = Path.Combine(_tempFolder, "Output");
                    if (!Directory.Exists(outputDir))
                    {
                        Directory.CreateDirectory(outputDir);
                    }

                    string outputFile = _processor.ProcessVideo(videoFiles, bgMusicFile, speed, outputDir);
                    videoTask.OutputFilePath = outputFile;
                    videoTask.Status = "completed";
                    Console.WriteLine(string.Format("[SUCCESS] Task {0} completed successfully. Output file: {1}", taskId, outputFile));
                }
                catch (Exception ex)
                {
                    videoTask.Status = "failed";
                    videoTask.ErrorMessage = ex.Message;
                    Console.WriteLine(string.Format("[ERROR] Task {0} failed: {1}", taskId, ex.Message));
                }
                finally
                {
                    // Clean up individual source video/audio uploads in task folder
                    try
                    {
                        if (Directory.Exists(taskFolder))
                        {
                            Directory.Delete(taskFolder, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(string.Format("[WARNING] Failed to clean task folder {0}: {1}", taskFolder, ex.Message));
                    }
                }
            });

            // Return processing status with task ID immediately
            response.StatusCode = (int)HttpStatusCode.Accepted;
            SendJson(response, string.Format("{{\"status\": \"processing\", \"taskId\": \"{0}\"}}", taskId));
        }

        private void HandleStatus(HttpListenerRequest request, HttpListenerResponse response)
        {
            var query = request.QueryString;
            string id = query["id"];

            VideoTask task;
            if (string.IsNullOrEmpty(id) || !Tasks.TryGetValue(id, out task))
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                SendJson(response, "{\"error\": \"Task not found\"}");
                return;
            }

            if (task.Status == "completed")
            {
                SendJson(response, string.Format("{{\"status\": \"completed\", \"taskId\": \"{0}\"}}", task.Id));
            }
            else if (task.Status == "failed")
            {
                // Escape error string for JSON compatibility
                string escapedError = null;
                if (task.ErrorMessage != null)
                {
                    escapedError = task.ErrorMessage.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
                }
                SendJson(response, string.Format("{{\"status\": \"failed\", \"error\": \"{0}\"}}", escapedError));
            }
            else
            {
                SendJson(response, "{\"status\": \"processing\"}");
            }
        }

        private void HandleDownload(HttpListenerRequest request, HttpListenerResponse response)
        {
            var query = request.QueryString;
            string id = query["id"];

            VideoTask task;
            if (string.IsNullOrEmpty(id) || !Tasks.TryGetValue(id, out task) || task.Status != "completed" || string.IsNullOrEmpty(task.OutputFilePath))
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                SendJson(response, "{\"error\": \"Task output not found or not completed yet\"}");
                return;
            }

            if (!File.Exists(task.OutputFilePath))
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                SendJson(response, "{\"error\": \"The processed video file was not found on the server\"}");
                return;
            }

            try
            {
                response.ContentType = "video/mp4";
                response.Headers.Add("Content-Disposition", "attachment; filename=\"processed_video.mp4\"");

                using (var fs = new FileStream(task.OutputFilePath, FileMode.Open, FileAccess.Read))
                {
                    response.ContentLength64 = fs.Length;
                    fs.CopyTo(response.OutputStream);
                }
                response.OutputStream.Close();
                response.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ERROR] Download error: " + ex.Message);
            }
        }

        private void SendString(HttpListenerResponse response, string content, string contentType)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(content);
            response.ContentType = contentType;
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();
            response.Close();
        }

        private void SendJson(HttpListenerResponse response, string json)
        {
            SendString(response, json, "application/json; charset=utf-8");
        }

        private void CleanTempDirectory()
        {
            try
            {
                if (Directory.Exists(_tempFolder))
                {
                    // Recursively delete temporary uploads and old completed videos
                    foreach (var dir in Directory.GetDirectories(_tempFolder))
                    {
                        Directory.Delete(dir, true);
                    }
                    foreach (var file in Directory.GetFiles(_tempFolder))
                    {
                        File.Delete(file);
                    }
                    Console.WriteLine("[INFO] Temp directory cleaned successfully.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[WARNING] Failed to clean temp directory on start: " + ex.Message);
            }
        }

        private string GetEmbeddedFrontendHtml()
        {
            return @"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Video Speed & Music Fusion Engine</title>
    <style>
        :root {
            --primary: #4f46e5;
            --primary-hover: #4338ca;
            --background: #0f172a;
            --surface: #1e293b;
            --surface-hover: #334155;
            --text: #f8fafc;
            --text-secondary: #94a3b8;
            --success: #10b981;
            --error: #ef4444;
            --border: #475569;
        }

        * {
            box-sizing: border-box;
            margin: 0;
            padding: 0;
            font-family: 'Segoe UI', -apple-system, BlinkMacSystemFont, Roboto, Oxygen, Ubuntu, Cantarell, 'Open Sans', sans-serif;
        }

        body {
            background-color: var(--background);
            color: var(--text);
            min-height: 100vh;
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center;
            padding: 2rem 1rem;
        }

        .container {
            width: 100%;
            max-width: 800px;
            background-color: var(--surface);
            border-radius: 16px;
            box-shadow: 0 10px 25px rgba(0, 0, 0, 0.4);
            border: 1px solid var(--border);
            padding: 2.5rem;
            display: flex;
            flex-direction: column;
            gap: 2rem;
        }

        header {
            text-align: center;
            border-bottom: 1px solid var(--border);
            padding-bottom: 1.5rem;
        }

        header h1 {
            font-size: 2.2rem;
            font-weight: 800;
            background: linear-gradient(to right, #6366f1, #a855f7);
            -webkit-background-clip: text;
            -webkit-text-fill-color: transparent;
            margin-bottom: 0.5rem;
        }

        header p {
            color: var(--text-secondary);
            font-size: 1rem;
        }

        .upload-section {
            display: grid;
            grid-template-columns: 1fr;
            gap: 1.5rem;
        }

        @media (min-width: 640px) {
            .upload-section {
                grid-template-columns: 1fr 1fr;
            }
        }

        .upload-card {
            background-color: rgba(15, 23, 42, 0.5);
            border: 2px dashed var(--border);
            border-radius: 12px;
            padding: 1.5rem;
            text-align: center;
            cursor: pointer;
            transition: all 0.3s ease;
            position: relative;
        }

        .upload-card:hover {
            border-color: var(--primary);
            background-color: rgba(15, 23, 42, 0.8);
        }

        .upload-card svg {
            width: 48px;
            height: 48px;
            stroke: var(--primary);
            fill: none;
            margin-bottom: 1rem;
        }

        .upload-card h3 {
            font-size: 1.1rem;
            margin-bottom: 0.5rem;
        }

        .upload-card p {
            font-size: 0.85rem;
            color: var(--text-secondary);
        }

        .file-input {
            display: none;
        }

        .file-list {
            text-align: left;
            margin-top: 1rem;
            max-height: 150px;
            overflow-y: auto;
            display: flex;
            flex-direction: column;
            gap: 0.5rem;
        }

        .file-item {
            background-color: var(--surface-hover);
            padding: 0.5rem 0.75rem;
            border-radius: 6px;
            font-size: 0.85rem;
            display: flex;
            justify-content: space-between;
            align-items: center;
            border-left: 3px solid var(--primary);
        }

        .file-item.audio {
            border-left-color: #a855f7;
        }

        .remove-file {
            background: none;
            border: none;
            color: var(--error);
            font-size: 1.1rem;
            cursor: pointer;
            line-height: 1;
        }

        .remove-file:hover {
            opacity: 0.8;
        }

        .options-section {
            background-color: rgba(15, 23, 42, 0.5);
            border-radius: 12px;
            padding: 1.5rem;
            border: 1px solid var(--border);
            display: flex;
            flex-direction: column;
            gap: 1rem;
        }

        .options-grid {
            display: grid;
            grid-template-columns: 1fr;
            gap: 1rem;
        }

        @media (min-width: 640px) {
            .options-grid {
                grid-template-columns: 1fr;
            }
        }

        .option-group {
            display: flex;
            flex-direction: column;
            gap: 0.5rem;
        }

        .option-group label {
            font-size: 0.9rem;
            font-weight: 600;
            color: var(--text-secondary);
        }

        .speed-selector {
            display: flex;
            gap: 1rem;
        }

        .speed-btn {
            flex: 1;
            background-color: var(--surface-hover);
            border: 1px solid var(--border);
            color: var(--text);
            padding: 0.75rem;
            border-radius: 8px;
            cursor: pointer;
            font-weight: bold;
            transition: all 0.2s ease;
        }

        .speed-btn:hover {
            border-color: var(--primary);
        }

        .speed-btn.active {
            background-color: var(--primary);
            border-color: var(--primary);
        }

        .process-btn {
            background: linear-gradient(to right, var(--primary), #6366f1);
            color: var(--text);
            border: none;
            border-radius: 10px;
            padding: 1rem;
            font-size: 1.1rem;
            font-weight: bold;
            cursor: pointer;
            transition: transform 0.2s ease, opacity 0.2s ease;
            box-shadow: 0 4px 15px rgba(79, 70, 229, 0.4);
            display: flex;
            justify-content: center;
            align-items: center;
            gap: 0.5rem;
        }

        .process-btn:hover {
            transform: translateY(-2px);
            opacity: 0.95;
        }

        .process-btn:disabled {
            background: var(--border);
            box-shadow: none;
            cursor: not-allowed;
            transform: none;
        }

        .status-container {
            display: none;
            background-color: rgba(15, 23, 42, 0.5);
            border: 1px dashed var(--border);
            border-radius: 12px;
            padding: 1.5rem;
            text-align: center;
            flex-direction: column;
            align-items: center;
            gap: 1rem;
        }

        .spinner {
            width: 50px;
            height: 50px;
            border: 5px solid rgba(79, 70, 229, 0.2);
            border-top-color: var(--primary);
            border-radius: 50%;
            animation: spin 1s linear infinite;
        }

        @keyframes spin {
            to { transform: rotate(360deg); }
        }

        .status-text {
            font-size: 1.1rem;
            font-weight: 600;
        }

        .status-subtext {
            color: var(--text-secondary);
            font-size: 0.85rem;
        }

        .download-btn {
            background: linear-gradient(to right, var(--success), #34d399);
            color: var(--text);
            border: none;
            border-radius: 8px;
            padding: 0.75rem 1.5rem;
            font-size: 1rem;
            font-weight: bold;
            cursor: pointer;
            text-decoration: none;
            transition: transform 0.2s;
            display: inline-flex;
            align-items: center;
            gap: 0.5rem;
            box-shadow: 0 4px 12px rgba(16, 185, 129, 0.3);
            margin-top: 0.5rem;
        }

        .download-btn:hover {
            transform: translateY(-2px);
        }

        .error-message {
            color: var(--error);
            background-color: rgba(239, 68, 68, 0.1);
            border: 1px solid var(--error);
            padding: 0.75rem;
            border-radius: 8px;
            font-size: 0.9rem;
            margin-top: 1rem;
            display: none;
            text-align: left;
            word-break: break-word;
        }

        footer {
            margin-top: 2rem;
            color: var(--text-secondary);
            font-size: 0.85rem;
            text-align: center;
        }
    </style>
</head>
<body>
    <div class=""container"">
        <header>
            <h1>Video & Music Fusion Engine</h1>
            <p>Join multiple videos, increase speed, and bind with custom background music in seconds.</p>
        </header>

        <div class=""upload-section"">
            <!-- Video Upload -->
            <div class=""upload-card"" onclick=""document.getElementById('videoInput').click()"">
                <svg viewBox=""0 0 24 24"">
                    <path stroke-linecap=""round"" stroke-linejoin=""round"" stroke-width=""2"" d=""M15 10l4.553-2.276A1 1 0 0121 8.618v6.764a1 1 0 01-1.447.894L15 14M5 18h8a2 2 0 002-2V8a2 2 0 00-2-2H5a2 2 0 00-2 2v8a2 2 0 002 2z"" />
                </svg>
                <h3>Upload Videos</h3>
                <p>Select videos to merge. Drag and drop supported.</p>
                <p style=""margin-top:0.25rem; font-size:0.75rem; color:#6366f1;"">Videos will be merged in the order they are added</p>
                <input type=""file"" id=""videoInput"" class=""file-input"" multiple accept=""video/*"">
                <div class=""file-list"" id=""videoList"" onclick=""event.stopPropagation()""></div>
            </div>

            <!-- Music Upload -->
            <div class=""upload-card"" onclick=""document.getElementById('audioInput').click()"">
                <svg viewBox=""0 0 24 24"">
                    <path stroke-linecap=""round"" stroke-linejoin=""round"" stroke-width=""2"" d=""M9 19V6l12-3v13M9 19c0 1.105-1.343 2-3 2s-3-.895-3-2 1.343-2 3-2 3 .895 3 2zm12-3c0 1.105-1.343 2-3 2s-3-.895-3-2 1.343-2 3-2 3 .895 3 2zM9 10l12-3"" />
                </svg>
                <h3>Upload Background Music</h3>
                <p>Select one audio track (MP3/WAV/AAC).</p>
                <input type=""file"" id=""audioInput"" class=""file-input"" accept=""audio/*"">
                <div class=""file-list"" id=""audioList"" onclick=""event.stopPropagation()""></div>
            </div>
        </div>

        <div class=""options-section"">
            <div class=""options-grid"">
                <div class=""option-group"">
                    <label>Select Speed Multiplier</label>
                    <div class=""speed-selector"">
                        <button type=""button"" class=""speed-btn active"" onclick=""setSpeed(2)"">2X Speed</button>
                        <button type=""button"" class=""speed-btn"" onclick=""setSpeed(3)"">3X Speed</button>
                    </div>
                </div>
            </div>

            <button type=""button"" class=""process-btn"" id=""processBtn"" onclick=""startProcessing()"" disabled>
                Fuse & Speed Up Video
            </button>
        </div>

        <div class=""status-container"" id=""statusContainer"">
            <div class=""spinner"" id=""statusSpinner""></div>
            <div class=""status-text"" id=""statusText"">Parsing files and starting stream...</div>
            <div class=""status-subtext"" id=""statusSubtext"">This will take a few moments depending on video size.</div>
            <div id=""downloadPlaceholder""></div>
        </div>

        <div class=""error-message"" id=""errorMessage""></div>
    </div>

    <footer>
        <p>&copy; 2026 Video & Music Fusion Engine. Self-Hosted High-Performance C# Core Pipeline.</p>
    </footer>

    <script>
        let selectedVideos = [];
        let selectedAudio = null;
        let selectedSpeed = 2;
        let activeTaskId = null;
        let pollInterval = null;

        // Elements
        const videoInput = document.getElementById('videoInput');
        const audioInput = document.getElementById('audioInput');
        const videoList = document.getElementById('videoList');
        const audioList = document.getElementById('audioList');
        const processBtn = document.getElementById('processBtn');
        const statusContainer = document.getElementById('statusContainer');
        const statusSpinner = document.getElementById('statusSpinner');
        const statusText = document.getElementById('statusText');
        const statusSubtext = document.getElementById('statusSubtext');
        const downloadPlaceholder = document.getElementById('downloadPlaceholder');
        const errorMessage = document.getElementById('errorMessage');

        // Drag and drop handling
        document.querySelectorAll('.upload-card').forEach((card, index) => {
            card.addEventListener('dragover', (e) => {
                e.preventDefault();
                card.style.borderColor = 'var(--primary)';
            });

            card.addEventListener('dragleave', () => {
                card.style.borderColor = 'var(--border)';
            });

            card.addEventListener('drop', (e) => {
                e.preventDefault();
                card.style.borderColor = 'var(--border)';
                const files = e.dataTransfer.files;
                if (index === 0) {
                    addVideos(files);
                } else {
                    if (files.length > 0) addAudio(files[0]);
                }
            });
        });

        videoInput.addEventListener('change', (e) => {
            addVideos(e.target.files);
        });

        audioInput.addEventListener('change', (e) => {
            if (e.target.files.length > 0) {
                addAudio(e.target.files[0]);
            }
        });

        function addVideos(files) {
            for (let i = 0; i < files.length; i++) {
                const file = files[i];
                if (file.type.startsWith('video/')) {
                    selectedVideos.push(file);
                }
            }
            renderFileList();
            validateForm();
        }

        function removeVideo(index) {
            selectedVideos.splice(index, 1);
            renderFileList();
            validateForm();
        }

        function addAudio(file) {
            if (file.type.startsWith('audio/')) {
                selectedAudio = file;
            } else {
                alert('Please upload a valid audio file.');
            }
            renderFileList();
            validateForm();
        }

        function removeAudio() {
            selectedAudio = null;
            renderFileList();
            validateForm();
        }

        function renderFileList() {
            // Render video list
            videoList.innerHTML = '';
            selectedVideos.forEach((file, idx) => {
                const item = document.createElement('div');
                item.className = 'file-item';
                item.innerHTML = `
                    <span>${idx + 1}. ${file.name} (${(file.size / (1024 * 1024)).toFixed(1)} MB)</span>
                    <button class=""remove-file"" onclick=""removeVideo(${idx})"">&times;</button>
                `;
                videoList.appendChild(item);
            });

            // Render audio list
            audioList.innerHTML = '';
            if (selectedAudio) {
                const item = document.createElement('div');
                item.className = 'file-item audio';
                item.innerHTML = `
                    <span>${selectedAudio.name} (${(selectedAudio.size / (1024 * 1024)).toFixed(1)} MB)</span>
                    <button class=""remove-file"" onclick=""removeAudio()"">&times;</button>
                `;
                audioList.appendChild(item);
            }
        }

        function setSpeed(speed) {
            selectedSpeed = speed;
            document.querySelectorAll('.speed-btn').forEach(btn => {
                btn.classList.remove('active');
            });
            event.target.classList.add('active');
        }

        function validateForm() {
            const isValid = selectedVideos.length > 0 && selectedAudio !== null;
            processBtn.disabled = !isValid;
        }

        function startProcessing() {
            errorMessage.style.display = 'none';
            statusContainer.style.display = 'flex';
            statusSpinner.style.display = 'block';
            statusText.innerText = 'Uploading your media files...';
            statusSubtext.innerText = 'Uploading streams to server directory. This might take a bit depending on file sizes.';
            downloadPlaceholder.innerHTML = '';
            processBtn.disabled = true;

            const formData = new FormData();
            formData.append('speed', selectedSpeed);
            
            selectedVideos.forEach((file, idx) => {
                // Ensure name is formatted 'video_XXXX' for proper sorting
                const nameIdx = String(idx).padStart(4, '0');
                formData.append(`video_${nameIdx}`, file);
            });

            formData.append('bgMusic', selectedAudio);

            const xhr = new XMLHttpRequest();
            xhr.open('POST', '/api/upload', true);

            // Upload progress tracking
            xhr.upload.onprogress = function(e) {
                if (e.lengthComputable) {
                    const pct = Math.round((e.loaded / e.total) * 100);
                    statusText.innerText = `Uploading: ${pct}%`;
                }
            };

            xhr.onload = function() {
                if (xhr.status === 202) {
                    try {
                        const res = JSON.parse(xhr.responseText);
                        activeTaskId = res.taskId;
                        statusText.innerText = 'Media uploaded! Fusing and speeding up...';
                        statusSubtext.innerText = 'The server is now executing high-performance ffmpeg filters. Please do not close this window.';
                        // Start polling status
                        pollInterval = setInterval(checkStatus, 2000);
                    } catch (err) {
                        showError('Invalid server response during upload: ' + err.message);
                    }
                } else {
                    try {
                        const errRes = JSON.parse(xhr.responseText);
                        showError(errRes.error || 'Upload failed');
                    } catch {
                        showError('Upload failed with status code: ' + xhr.status);
                    }
                }
            };

            xhr.onerror = function() {
                showError('Network error occurred during upload. Please check your network connection.');
            };

            xhr.send(formData);
        }

        function checkStatus() {
            if (!activeTaskId) return;

            fetch(`/api/status?id=${activeTaskId}`)
                .then(res => res.json())
                .then(data => {
                    if (data.status === 'completed') {
                        clearInterval(pollInterval);
                        showSuccess();
                    } else if (data.status === 'failed') {
                        clearInterval(pollInterval);
                        showError('Video Processing Failed: ' + data.error);
                    }
                })
                .catch(err => {
                    console.warn('Status polling error:', err);
                });
        }

        function showSuccess() {
            statusSpinner.style.display = 'none';
            statusText.innerText = '🎉 Processing Complete!';
            statusSubtext.innerText = 'Your sped-up video with background music has been compiled successfully.';
            
            downloadPlaceholder.innerHTML = `
                <a href=""/api/download?id=${activeTaskId}"" class=""download-btn"">
                    <svg style=""width:20px;height:20px;fill:currentColor;"" viewBox=""0 0 20 20"">
                        <path fill-rule=""evenodd"" d=""M3 17a1 1 0 011-1h12a1 1 0 110 2H4a1 1 0 01-1-1zm3.293-7.707a1 1 0 011.414 0L9 10.586V3a1 1 0 112 0v7.586l1.293-1.293a1 1 0 111.414 1.414l-3 3a1 1 0 01-1.414 0l-3-3a1 1 0 010-1.414z"" clip-rule=""evenodd"" />
                    </svg>
                    Download Compiled MP4
                </a>
            `;
            validateForm();
        }

        function showError(msg) {
            if (pollInterval) clearInterval(pollInterval);
            statusContainer.style.display = 'none';
            errorMessage.innerText = msg;
            errorMessage.style.display = 'block';
            validateForm();
        }
    </script>
</body>
</html>";
        }
    }
}
