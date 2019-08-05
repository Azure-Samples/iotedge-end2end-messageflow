namespace MessageForwarder
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.Loader;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.ApplicationInsights;
    using Microsoft.Azure.Devices.Client;
    using Newtonsoft.Json;
    using Serilog;
    using Serilog.Configuration;
    using Serilog.Core;
    using Serilog.Events;

    class Program
    {
        // AppInsights TelemetryClient
        // Note: In "real-life" Edge modules, the use of AppInsights might not be ideal if the Edge is supposed to be running fully or partially offline
        // Thus, AppInsights is not used here for the actual module logging
        private static TelemetryClient telemetry = new TelemetryClient();

        private static int counter = 0;
        private static int HeartbeatRateMs = 300000;
        private static CancellationTokenSource _cts;
        public static int Main() => MainAsync().Result;

        static async Task<int> MainAsync()
        {
            InitLogging();
            Log.Information($"Module {Environment.GetEnvironmentVariable("IOTEDGE_MODULEID")} starting up...");
            var moduleClient = await Init();

            // Register direct method handlers
            await moduleClient.SetMethodDefaultHandlerAsync(DefaultMethodHandler, moduleClient);

            // Register message input handler
            await moduleClient.SetInputMessageHandlerAsync("input1", PipeMessage, moduleClient);

            _cts = new CancellationTokenSource();
            AssemblyLoadContext.Default.Unloading += (ctx) => _cts.Cancel();
            Console.CancelKeyPress += (sender, cpe) => _cts.Cancel();

            var heartbeatEnv = Environment.GetEnvironmentVariable("HeartbeatRateMs");
            if (!string.IsNullOrEmpty(heartbeatEnv))
            {
                HeartbeatRateMs = int.Parse(heartbeatEnv);
            }
            await SendHeartbeatForever(moduleClient, _cts.Token);

            // Wait until the app unloads or is cancelled
            await WhenCancelled(_cts.Token);
            return 0;
        }

        /// <summary>
        /// Handles cleanup operations when app is cancelled or unloads
        /// </summary>
        public static Task WhenCancelled(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).SetResult(true), tcs);
            return tcs.Task;
        }

        /// <summary>
        /// Initializes the ModuleClient
        /// </summary>
        static async Task<ModuleClient> Init()
        {
            var transportType = TransportType.Amqp_Tcp_Only;
            string transportProtocol = Environment.GetEnvironmentVariable("ClientTransportType");

            // The way the module connects to the EdgeHub can be controlled via the env variable. Either MQTT or AMQP
            if (!string.IsNullOrEmpty(transportProtocol))
            {
                switch (transportProtocol.ToUpper())
                {
                    case "AMQP":
                        transportType = TransportType.Amqp_Tcp_Only;
                        break;
                    case "MQTT":
                        transportType = TransportType.Mqtt_Tcp_Only;
                        break;
                    default:
                        // Anything else: use default
                        Log.Warning($"Ignoring unknown TransportProtocol={transportProtocol}. Using default={transportType}");
                        break;
                }
            }

            // Open a connection to the Edge runtime
            ModuleClient moduleClient = await ModuleClient.CreateFromEnvironmentAsync(transportType);
            moduleClient.SetConnectionStatusChangesHandler(ConnectionStatusHandler);

            await moduleClient.OpenAsync();
            Log.Information($"Edge Hub module client initialized using {transportType}");

            return moduleClient;
        }

        /// <summary>
        /// Callback for whenever the connection status changes
        /// Mostly we just log the new status and the reason. 
        /// But for some disconnects we need to handle them here differently for our module to recover
        /// </summary>
        /// <param name="status"></param>
        /// <param name="reason"></param>
        private static void ConnectionStatusHandler(ConnectionStatus status, ConnectionStatusChangeReason reason)
        {
            Log.Information($"Module connection changed. New status={status.ToString()} Reason={reason.ToString()}");

            // Sometimes the connection can not be recovered if it is in either of those states.
            // To solve this, we exit the module. The Edge Agent will then restart it (retrying with backoff)
            if (reason == ConnectionStatusChangeReason.Retry_Expired || reason == ConnectionStatusChangeReason.Client_Close)
            {
                Log.Error($"Connection can not be re-established. Exiting module");
                _cts?.Cancel();
            }
        }

        /// <summary>
        /// Fallback method handler for any method calls which are not implemented
        /// </summary>
        /// <param name="methodRequest"></param>
        /// <param name="userContext"></param>
        /// <returns></returns>
        private static Task<MethodResponse> DefaultMethodHandler(MethodRequest methodRequest, object userContext)
        {
            Log.Information($"Received method invocation for non-existing method {methodRequest.Name}. Returning 404.");
            dynamic result = new { ModuleResponse = $"Method {methodRequest.Name} not implemented" };
            var outResult = JsonConvert.SerializeObject(result);
            return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(outResult), 404));
        }

        /// <summary>
        /// This method is called whenever the module is receiving a message from the EdgeHub. 
        /// It just pipes the messages without any change.
        /// </summary>
        static async Task<MessageResponse> PipeMessage(Message message, object userContext)
        {
            int counterValue = Interlocked.Increment(ref counter);

            var moduleClient = userContext as ModuleClient;

            byte[] messageBytes = message.GetBytes();
            string messageString = Encoding.UTF8.GetString(messageBytes);
            Log.Information($"Received message - Counter: {counterValue}, Body: [{messageString}]");

            if (message.Properties.ContainsKey("correlationId"))
            {
                var correlationId = message.Properties["correlationId"];
                Log.Information($"CorrelationId={correlationId}");

                var telemetryProperties = new Dictionary<string, string>
                {
                    { "correlationId", correlationId },
                    { "processingStep", "30-MessageForwarderModule"},
                    { "edgeModuleId", Environment.GetEnvironmentVariable("IOTEDGE_MODULEID") }
                };
                telemetry.TrackEvent("30-ReceivedMessage", telemetryProperties);

                var forwardedMessage = new Message(messageBytes);
                forwardedMessage.ContentType = "application/json";
                forwardedMessage.ContentEncoding = "UTF-8";
                forwardedMessage.Properties.Add("correlationId", correlationId);
                forwardedMessage.Properties.Add("scope", "end2end");

                try
                {
                    await moduleClient.SendEventAsync("output1", forwardedMessage);
                    telemetry.TrackEvent("31-MessageSentToEdgeHub", telemetryProperties);
                    Log.Information("Received message forwarded");
                }
                catch (Exception e)
                {
                    Log.Error(e, "Error during message sending to Edge Hub");
                    telemetry.TrackEvent("35-ErrorMessageNotSentToEdgeHub", telemetryProperties);
                }
            }
            else
            {
                Log.Warning("Message received without correlationId property");
            }
            return MessageResponse.Completed;
        }

        /// <summary>
        /// This method sends a new heartbeat message every x seconds
        /// </summary>
        private static async Task SendHeartbeatForever(ModuleClient moduleClient, CancellationToken cancellationToken)
        {
            // Read ModuleId from env
            string moduleId = Environment.GetEnvironmentVariable("IOTEDGE_MODULEID");

            while (!cancellationToken.IsCancellationRequested)
            {
                var correlationId = Guid.NewGuid().ToString();
                Log.Information($"New Heartbeat - CorrelationId={correlationId}");

                var telemetryProperties = new Dictionary<string, string>
                {
                    { "correlationId", correlationId },
                    { "processingStep", "80-Heartbeat-MessageForwarder"},
                    { "edgeModuleId", Environment.GetEnvironmentVariable("IOTEDGE_MODULEID") }
                };
                telemetry.TrackEvent("80-Heartbeat-MessageForwarder", telemetryProperties);

                var heartbeatMessage = new Message(Encoding.UTF8.GetBytes("{\"heartbeat\": true}"));
                heartbeatMessage.ContentType = "application/json";
                heartbeatMessage.ContentEncoding = "UTF-8";
                heartbeatMessage.Properties.Add("correlationId", correlationId);
                heartbeatMessage.Properties.Add("scope", "end2end");
                heartbeatMessage.Properties.Add("heartbeat", "true");

                try
                {
                    await moduleClient.SendEventAsync("output1", heartbeatMessage);
                    telemetry.TrackEvent("81-Heartbeat-Sent-MessageForwarder", telemetryProperties);
                    Log.Information("Heartbeat message sent");
                }
                catch (Exception e)
                {
                    Log.Error(e, "Error during message sending to Edge Hub");
                    telemetry.TrackEvent("85-ErrorHeartbeatMessageNotSentToEdgeHub", telemetryProperties);
                }

                // Sleep for duration of HeartbeatRateMs
                await Task.Delay(HeartbeatRateMs, cancellationToken);
            }
        }

        /// <summary>
        /// Initialize logging using Serilog
        /// LogLevel can be controlled via RuntimeLogLevel env var
        /// </summary>
        private static void InitLogging()
        {
            LoggerConfiguration loggerConfiguration = new LoggerConfiguration();

            var logLevel = Environment.GetEnvironmentVariable("RuntimeLogLevel");
            logLevel = !string.IsNullOrEmpty(logLevel) ? logLevel.ToLower() : "info";

            // set the log level
            switch (logLevel)
            {
                case "fatal":
                    loggerConfiguration.MinimumLevel.Fatal();
                    break;
                case "error":
                    loggerConfiguration.MinimumLevel.Error();
                    break;
                case "warn":
                    loggerConfiguration.MinimumLevel.Warning();
                    break;
                case "info":
                    loggerConfiguration.MinimumLevel.Information();
                    break;
                case "debug":
                    loggerConfiguration.MinimumLevel.Debug();
                    break;
                case "verbose":
                    loggerConfiguration.MinimumLevel.Verbose();
                    break;
            }

            // set logging sinks
            loggerConfiguration.WriteTo.Console(outputTemplate: "<{Severity}> {Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] - {Message}{NewLine}{Exception}");
            loggerConfiguration.Enrich.With(SeverityEnricher.Instance);
            loggerConfiguration.Enrich.FromLogContext();
            Log.Logger = loggerConfiguration.CreateLogger();
            Log.Information($"Initializied logger with log level {logLevel}");
        }


    }

    // This maps the Edge log level to the severity level based on Syslog severity levels.
    // https://en.wikipedia.org/wiki/Syslog#Severity_level
    // This allows tools to parse the severity level from the log text and use it to enhance the log
    // For example errors can show up as red
    class SeverityEnricher : ILogEventEnricher
    {
        static readonly IDictionary<LogEventLevel, int> LogLevelSeverityMap = new Dictionary<LogEventLevel, int>
        {
            [LogEventLevel.Fatal] = 0,
            [LogEventLevel.Error] = 3,
            [LogEventLevel.Warning] = 4,
            [LogEventLevel.Information] = 6,
            [LogEventLevel.Debug] = 7,
            [LogEventLevel.Verbose] = 7
        };

        SeverityEnricher()
        {
        }

        public static SeverityEnricher Instance => new SeverityEnricher();

        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) =>
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(
                "Severity", LogLevelSeverityMap[logEvent.Level]));
    }
}
