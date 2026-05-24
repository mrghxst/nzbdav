namespace NzbWebDAV.Api.Controllers.TestUsenetSpeed;

public class TestUsenetSpeedResponse : BaseApiResponse
{
    public bool Success { get; set; }
    public double SpeedMBps { get; set; }
    public double SpeedMBpsPerConnection { get; set; }
    public int ConnectionsUsed { get; set; }
}
