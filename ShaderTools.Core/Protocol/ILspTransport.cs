using Newtonsoft.Json.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ShaderTools.Core.Protocol;

public interface ILspTransport
{
    Task<JObject?> ReadAsync(CancellationToken token);
    Task WriteAsync(JObject message, CancellationToken token);
}

internal sealed class StreamLspTransport : ILspTransport
{
    private readonly Stream input;
    private readonly Stream output;

    public StreamLspTransport(Stream input, Stream output) { this.input = input; this.output = output; }
    public Task<JObject?> ReadAsync(CancellationToken token) => LspFraming.ReadAsync(output, token);
    public Task WriteAsync(JObject message, CancellationToken token) => LspFraming.WriteAsync(input, message, token);
}
