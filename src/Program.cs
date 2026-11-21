using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Threading;

namespace VideoProcessorApp
{
    class Program
    {
        private const string FfmpegDownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
        private static readonly string TargetFfmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");

        static void Main(string[] args)
        {
            Console.WriteLine("=================================================");
            Console.WriteLine("  Video & Music Fusion Engine - Bootstrap Active  ");
            Console.WriteLine("=================================================");

            // Configure modern TLS protocols for SSL handshakes
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

            var processor = new Processor();
            if (!processor.IsFFmpegAvailable())
            {
                Console.WriteLine("[INFO] FFmpeg was not detected locally. Initializing automatic download...");
                if (!DownloadAndExtractFfmpeg())
                {
                    Console.WriteLine("[ERROR] Could not download FFmpeg automatically.");
                    Console.WriteLine("[ERROR] Please manually download FFmpeg and place 'ffmpeg.exe' in: " + AppDomain.CurrentDomain.BaseDirectory);
                    Console.WriteLine("Press any key to exit...");
                    Console.ReadKey();
                    return;
                }
            }
            else
            {
                Console.WriteLine("[SUCCESS] FFmpeg engine is verified and ready.");
            }

            int port = 5000;
            int customPort;
            if (args.Length > 0 && int.TryParse(args[0], out customPort))
            {
                port = customPort;
            }

            var server = new WebServer(port);
            try
            {
                server.Start();
                Console.WriteLine("=================================================");
                Console.WriteLine("[STATUS] Web service is fully operational.");
                Console.WriteLine("[STATUS] Open your browser and navigate to: http://localhost:" + port);
                Console.WriteLine("[STATUS] Press Ctrl+C to stop the server.");
                Console.WriteLine("=================================================");

                // Keep the thread alive
                var keepAliveEvent = new ManualResetEvent(false);
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    Console.WriteLine("\n[INFO] Shutting down web server...");
                    server.Stop();
                    keepAliveEvent.Set();
                };

                keepAliveEvent.WaitOne();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FATAL] Failed to start web server: " + ex.Message);
                Console.WriteLine("Please ensure you run this application as Administrator or with proper privileges.");
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
            }
        }

        private static bool DownloadAndExtractFfmpeg()
        {
            string tempZipPath = Path.Combine(Path.GetTempPath(), "ffmpeg_download_" + Guid.NewGuid().ToString("N") + ".zip");
            
            try
            {
                using (var client = new WebClient())
                {
                    client.Headers.Add("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                    
                    Console.WriteLine("[DOWNLOAD] Downloading FFmpeg Static Build from gyan.dev...");
                    Console.WriteLine("[DOWNLOAD] File size is around 90MB. This might take a short moment...");

                    // Print download progress
                    client.DownloadProgressChanged += (sender, e) =>
                    {
                        Console.Write(string.Format("\r[DOWNLOAD] Progress: {0}% ({1}MB / {2}MB)...", 
                            e.ProgressPercentage, 
                            e.BytesReceived / 1024 / 1024, 
                            e.TotalBytesToReceive / 1024 / 1024));
                    };

                    client.DownloadFileTaskAsync(new Uri(FfmpegDownloadUrl), tempZipPath).Wait();
                    Console.WriteLine("\n[DOWNLOAD] Download complete. Verifying and extracting archive...");
                }

                // Extract only ffmpeg.exe from the downloaded ZIP
                using (ZipArchive archive = ZipFile.OpenRead(tempZipPath))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (entry.FullName.EndsWith("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine(string.Format("[EXTRACT] Found '{0}'. Extracting directly to base folder...", entry.FullName));
                            entry.ExtractToFile(TargetFfmpegPath, true);
                            Console.WriteLine("[SUCCESS] FFmpeg successfully extracted and registered.");
                            return true;
                        }
                    }
                }

                Console.WriteLine("[ERROR] Failed to locate 'ffmpeg.exe' inside the downloaded ZIP file.");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine("\n[ERROR] Exception occurred during FFmpeg installation: " + ex.Message);
                if (ex.InnerException != null)
                {
                    Console.WriteLine("[ERROR] Inner Details: " + ex.InnerException.Message);
                }
                return false;
            }
            finally
            {
                // Clean up the temporary ZIP file
                try
                {
                    if (File.Exists(tempZipPath))
                    {
                        File.Delete(tempZipPath);
                    }
                }
                catch { }
            }
        }
    }
}
