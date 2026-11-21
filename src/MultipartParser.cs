using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VideoProcessorApp
{
    public class MultipartParser
    {
        public class FilePart
        {
            public string Name { get; set; }
            public string Filename { get; set; }
            public string ContentType { get; set; }
            public string TempFilePath { get; set; }
        }

        public class ParseResult
        {
            public Dictionary<string, string> Fields { get; private set; }
            public List<FilePart> Files { get; private set; }

            public ParseResult()
            {
                Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Files = new List<FilePart>();
            }
        }

        private readonly Stream _stream;
        private readonly byte[] _boundaryBytes;
        private readonly string _tempFolder;

        public MultipartParser(Stream stream, string boundary, string tempFolder)
        {
            _stream = stream;
            // The boundaries in the body start with "--"
            _boundaryBytes = Encoding.UTF8.GetBytes("--" + boundary);
            _tempFolder = tempFolder;
            if (!Directory.Exists(_tempFolder))
            {
                Directory.CreateDirectory(_tempFolder);
            }
        }

        public ParseResult Parse()
        {
            var result = new ParseResult();
            var buffer = new byte[64 * 1024]; // 64KB read buffer
            var streamBuffer = new MemoryStream();
            
            // Read stream into our memory buffer to process
            int bytesRead;
            while ((bytesRead = _stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                streamBuffer.Write(buffer, 0, bytesRead);
            }

            byte[] data = streamBuffer.ToArray();
            streamBuffer.Dispose();

            int position = 0;
            while (position < data.Length)
            {
                // Find next boundary
                int boundaryIndex = FindPattern(data, _boundaryBytes, position);
                if (boundaryIndex == -1)
                {
                    break;
                }

                // Move past boundary
                position = boundaryIndex + _boundaryBytes.Length;

                // Check for terminal boundary "--"
                if (position + 1 < data.Length && data[position] == '-' && data[position + 1] == '-')
                {
                    break; // End of multipart stream
                }

                // Consume trailing CRLF after boundary
                if (position + 1 < data.Length && data[position] == '\r' && data[position + 1] == '\n')
                {
                    position += 2;
                }

                // Parse headers
                int headerEndIndex = FindPattern(data, Encoding.UTF8.GetBytes("\r\n\r\n"), position);
                if (headerEndIndex == -1)
                {
                    break;
                }

                string headerString = Encoding.UTF8.GetString(data, position, headerEndIndex - position);
                position = headerEndIndex + 4; // Move past \r\n\r\n

                var headers = ParseHeaders(headerString);

                // Find next boundary to determine content end
                int nextBoundaryIndex = FindPattern(data, _boundaryBytes, position);
                if (nextBoundaryIndex == -1)
                {
                    break;
                }

                // The content ends before the trailing CRLF of the boundary, so we subtract 2 bytes (\r\n)
                int contentEndIndex = nextBoundaryIndex;
                if (contentEndIndex - 2 >= position && data[contentEndIndex - 2] == '\r' && data[contentEndIndex - 1] == '\n')
                {
                    contentEndIndex -= 2;
                }

                int contentLen = contentEndIndex - position;
                if (contentLen < 0) contentLen = 0;

                string contentDisp;
                headers.TryGetValue("Content-Disposition", out contentDisp);
                string name = GetHeaderParameter(contentDisp, "name");
                string filename = GetHeaderParameter(contentDisp, "filename");

                if (!string.IsNullOrEmpty(filename))
                {
                    // It's a file
                    string tempFile = Path.Combine(_tempFolder, Guid.NewGuid().ToString("N") + Path.GetExtension(filename));
                    using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write))
                    {
                        fs.Write(data, position, contentLen);
                    }

                    string contentType;
                    headers.TryGetValue("Content-Type", out contentType);
                    result.Files.Add(new FilePart
                    {
                        Name = name,
                        Filename = filename,
                        ContentType = contentType != null ? contentType.Trim() : null,
                        TempFilePath = tempFile
                    });
                }
                else if (!string.IsNullOrEmpty(name))
                {
                    // It's a regular field
                    string val = Encoding.UTF8.GetString(data, position, contentLen);
                    result.Fields[name] = val;
                }

                position = nextBoundaryIndex;
            }

            return result;
        }

        private int FindPattern(byte[] source, byte[] pattern, int start)
        {
            for (int i = start; i <= source.Length - pattern.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (source[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    return i;
                }
            }
            return -1;
        }

        private Dictionary<string, string> ParseHeaders(string headerString)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var lines = headerString.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                int colonIdx = line.IndexOf(':');
                if (colonIdx > 0)
                {
                    string key = line.Substring(0, colonIdx).Trim();
                    string val = line.Substring(colonIdx + 1).Trim();
                    dict[key] = val;
                }
            }
            return dict;
        }

        private string GetHeaderParameter(string headerValue, string parameterName)
        {
            if (string.IsNullOrEmpty(headerValue)) return null;

            string searchKey = parameterName + "=";
            int idx = headerValue.IndexOf(searchKey, StringComparison.OrdinalIgnoreCase);
            if (idx == -1) return null;

            int start = idx + searchKey.Length;
            if (start < headerValue.Length && headerValue[start] == '"')
            {
                start++;
                int end = headerValue.IndexOf('"', start);
                if (end != -1)
                {
                    return headerValue.Substring(start, end - start);
                }
            }
            else
            {
                int end = headerValue.IndexOf(';', start);
                if (end != -1)
                {
                    return headerValue.Substring(start, end - start).Trim();
                }
                return headerValue.Substring(start).Trim();
            }
            return null;
        }
    }
}
