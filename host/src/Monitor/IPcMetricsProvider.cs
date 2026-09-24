namespace CodexToolsHost.Monitor
{
    public interface IPcMetricsProvider
    {
        PcMetricsSnapshot Read();
    }
}
