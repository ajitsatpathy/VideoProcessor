using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace VideoProcessorApp
{
    public class Processor
    {
        private readonly string _ffmpegPath;

        public Processor()
        {
            _ffmpegPath = FindFFmpeg();
        }

        public bool IsFFmpegAvailable()
        {
            return !string.IsNullOrEmpty(_ffmpegPath) && File.Exists(_ffmpegPath);
        }

        private string FindFFmpeg()
        {
            // Search paths:
            // 1. Current execution folder
            // 2. Project root folder
            // 3. System path
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string localPath = Path.Combine(baseDir, "ffmpeg.exe");
            if (File.Exists(localPath)) return localPath;

            string projectRootPath = Path.GetFullPath(Path.Combine(baseDir, @"..\..\ffmpeg.exe"));
            if (File.Exists(projectRootPath)) return projectRootPath;

            string rootPath = @"C:\workspace\VideoProcessor\ffmpeg.exe";
            if (File.Exists(rootPath)) return rootPath;

            // Check system PATH
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "where",
                        Arguments = "ffmpeg",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode == 0 && !string.IsNullOrEmpty(output))
                {
                    string firstLine = output.Split('\n')[0].Trim();
                    if (File.Exists(firstLine)) return firstLine;
                }
            }
            catch { }

            return null;
        }

        public string ProcessVideo(List<string> videoPaths, string bgMusicPath, double speed, string outputDirectory)
        {
            if (!IsFFmpegAvailable())
            {
                throw new FileNotFoundException("ffmpeg.exe was not found. Please ensure it is downloaded and placed in the application folder.");
            }

            if (videoPaths == null || videoPaths.Count == 0)
            {
                throw new ArgumentException("At least one video file is required.");
            }

            string outputFile = Path.Combine(outputDirectory, "processed_" + Guid.NewGuid().ToString("N") + ".mp4");
            
            // Build the FFmpeg command
            // We want to speed up each video and join them.
            // If background music is provided, we map it as the audio source, loop it, and use -shortest to match video length.
            var args = new StringBuilder();

            bool hasBgMusic = !string.IsNullOrEmpty(bgMusicPath) && File.Exists(bgMusicPath);

            // Inputs
            int videoStartInputIndex = 0;
            if (hasBgMusic)
            {
                // Input 0: Background music (looping)
                args.Append(string.Format("-stream_loop -1 -i \"{0}\" ", bgMusicPath));
                videoStartInputIndex = 1;
            }

            foreach (var video in videoPaths)
            {
                args.Append(string.Format("-i \"{0}\" ", video));
            }

            // Filter complex
            var filterComplex = new StringBuilder();
            filterComplex.Append("-filter_complex \"");

            // 1. Speed up each video input
            // e.g. [1:v]setpts=0.5*PTS[v1];
            double ptsMultiplier = 1.0 / speed;
            // Use 6 decimal places for precision
            string ptsStr = ptsMultiplier.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);

            for (int i = 0; i < videoPaths.Count; i++)
            {
                int inputIdx = videoStartInputIndex + i;
                filterComplex.Append(string.Format("[{0}:v]setpts={1}*PTS[v{2}]; ", inputIdx, ptsStr, i));
            }

            // 2. Concatenate the sped up videos
            // e.g. [v0][v1]concat=n=2:v=1:a=0[vout]
            for (int i = 0; i < videoPaths.Count; i++)
            {
                filterComplex.Append(string.Format("[v{0}]", i));
            }
            filterComplex.Append(string.Format("concat=n={0}:v=1:a=0[vout]\"", videoPaths.Count));

            args.Append(filterComplex.ToString());

            // Mapping outputs
            args.Append(" -map \"[vout]\" ");
            if (hasBgMusic)
            {
                // Map the looping background music (Input 0) and end when video ends
                args.Append("-map 0:a -shortest ");
            }

            // Codecs and output properties
            args.Append(string.Format("-c:v libx264 -pix_fmt yuv420p -preset fast -y \"{0}\"", outputFile));

            string arguments = args.ToString();
            Console.WriteLine("[INFO] Running FFmpeg with arguments: " + arguments);

            var startInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                var errorOutput = new StringBuilder();
                process.OutputDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine("[FFmpeg] " + e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) errorOutput.AppendLine(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    string errMsg = errorOutput.ToString();
                    Console.WriteLine("[ERROR] FFmpeg failed with exit code: " + process.ExitCode);
                    Console.WriteLine("[ERROR] FFmpeg Error: " + errMsg);
                    throw new Exception("FFmpeg processing failed: " + errMsg);
                }
            }

            return outputFile;
        }
    }
}
