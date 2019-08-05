using IoTHubTrigger = Microsoft.Azure.WebJobs.EventHubTriggerAttribute;
using System.Collections.Generic;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.EventHubs;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Edge.End2End
{
    public static class IotHubMessageProcessor
    {
        // AppInsights TelemetryClient
        private static TelemetryClient telemetry = new TelemetryClient();

        /// <summary>
        /// Function that processes messages from the IoT Hub events-endpoint
        /// </summary>
        /// <param name="message"></param>
        /// <param name="log"></param>
        [FunctionName("IotHubMessageProcessor")]
        public static void Run([IoTHubTrigger("messages/events", Connection = "iothubevents_cs", ConsumerGroup = "receiverfunction")]EventData message, ILogger log)
        {
            log.LogInformation($"IotHubMessageProcessor received a message: {Encoding.UTF8.GetString(message.Body.Array)}");

            if (message.Properties.ContainsKey("correlationId"))
            {
                var correlationId = message.Properties["correlationId"].ToString();
                log.LogInformation($"Message correlationId={correlationId}");

                if (message.Properties.ContainsKey("heartbeat"))
                {
                    log.LogInformation("Received hearbeat message. Not tracing this message.");
                }
                else
                {
                    var telemetryProperties = new Dictionary<string, string>
                    {
                        { "correlationId", correlationId },
                        { "processingStep", "100-IotHubMessageProcessor"}
                    };
                    telemetry.TrackEvent("100-ReceivedIoTHubMessage", telemetryProperties);
                }
            }
            else
            {
                log.LogWarning("Message received without correlationId property");
            }
        }
    }
}
