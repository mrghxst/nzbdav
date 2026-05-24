namespace NzbWebDAV.Api.Controllers.TestUsenetConnection;

public class TestUsenetConnectionResponse : BaseApiResponse
{
    public bool Connected { get; set; }
    public long LatencyMs { get; set; }
}