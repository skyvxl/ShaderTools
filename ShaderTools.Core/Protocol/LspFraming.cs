using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ShaderTools.Core.Protocol
{
    public static class LspFraming
    {
        private const int MaximumHeaderLength = 8192;
        private const int MaximumPayloadLength = 32 * 1024 * 1024;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        public static async Task<JObject?> ReadAsync(Stream stream, CancellationToken cancellationToken, bool allowUtf8Preamble = false)
        {
            StringBuilder header = new StringBuilder();
            byte[] single = new byte[1];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await stream.ReadAsync(single, 0, 1, cancellationToken).ConfigureAwait(false) == 0)
                {
                    if (header.Length == 0)
                    {
                        return null;
                    }

                    throw new EndOfStreamException("The language server closed a partial LSP header.");
                }
                // A .NET Framework Process can write its console encoding preamble while
                // opening redirected stdin. Accept it once at the VS connection boundary.
                if (allowUtf8Preamble && single[0] == 0xef)
                {
                    if (await stream.ReadAsync(single, 0, 1, cancellationToken).ConfigureAwait(false) != 1 || single[0] != 0xbb ||
                        await stream.ReadAsync(single, 0, 1, cancellationToken).ConfigureAwait(false) != 1 || single[0] != 0xbf)
                    {
                        throw new InvalidDataException("Invalid UTF-8 preamble in LSP header.");
                    }
                    allowUtf8Preamble = false;
                    continue;
                }
                allowUtf8Preamble = false;
                if (single[0] > 127)
                {
                    throw new InvalidDataException("LSP headers must be ASCII.");
                }

                _=header.Append((char)single[0]);
                int length = header.Length;
                if (length > MaximumHeaderLength)
                {
                    throw new InvalidDataException("LSP header is too large.");
                }

                if (length >= 4 && header[length - 4] == '\r' && header[length - 3] == '\n' &&
                    header[length - 2] == '\r' && header[length - 1] == '\n')
                {
                    break;
                }
            }

            int? contentLength = null;
            foreach (string line in header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    throw new InvalidDataException("Malformed LSP header.");
                }

                if (!line.Substring(0, colon).Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (contentLength.HasValue || !int.TryParse(line.Substring(colon + 1).Trim(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int parsed) || parsed <= 0 || parsed > MaximumPayloadLength)
                {
                    throw new InvalidDataException("Invalid or duplicate LSP Content-Length.");
                }

                contentLength = parsed;
            }
            if (!contentLength.HasValue)
            {
                throw new InvalidDataException("Missing LSP Content-Length.");
            }

            byte[] body = new byte[contentLength.Value];
            int offset = 0;
            while (offset < body.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = await stream.ReadAsync(body, offset, body.Length - offset, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("The language server closed a partial LSP message.");
                }

                offset += read;
            }
            try { return JObject.Parse(Utf8.GetString(body)); }
            catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
            { throw new InvalidDataException("Invalid UTF-8 JSON in an LSP message.", ex); }
        }

        // A connection must serialize calls to this method so that headers and bodies never interleave.
        public static async Task WriteAsync(Stream stream, JObject message, CancellationToken cancellationToken)
        {
            byte[] body = Utf8.GetBytes(message.ToString(Formatting.None));
            if (body.Length > MaximumPayloadLength)
            {
                throw new InvalidDataException("LSP message is too large.");
            }

            byte[] header = Encoding.ASCII.GetBytes("Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");
            await stream.WriteAsync(header, 0, header.Length, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, 0, body.Length, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
