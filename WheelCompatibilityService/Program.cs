using System.Diagnostics;
using XboxWheelCompatibility.WheelTransformer;

namespace XboxWheelCompatibility.WheelCompatibilityService
{
    internal static class Program
    {
        public static void Main(string[] arguments)
        {
            try
            {
                Host.CreateDefaultBuilder(arguments)
                    .UseWindowsService(options => options.ServiceName = "WheelCompatibilityService")
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<WheelInputTransformer>();
                        services.AddHostedService<WheelCompatibilityWorker>();
                    })
                    .Build()
                    .Run();
            }
            catch (Exception ex)
            {
                try
                {
                    EventLog.WriteEntry(".NET Runtime", ex.ToString(), EventLogEntryType.Error, 1000);
                }
                catch
                {
                    // Nothing else we can do if even the event log is unavailable.
                }
            }
        }
    }
}
