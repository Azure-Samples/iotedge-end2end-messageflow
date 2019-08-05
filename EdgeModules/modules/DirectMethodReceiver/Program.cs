namespace DirectMethodReceiver
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
        private static CancellationTokenSource _cts;

        public static int Main() => MainAsync().Result;

        static async Task<int> MainAsync()
        {
            InitLogging();
            Log.Information($"Module {Environment.GetEnvironmentVariable("IOTEDGE_MODULEID")} starting up...");
            var moduleClient = await Init();

            // Register direct method handlers
            await moduleClient.SetMethodHandlerAsync("NewMessageRequest", NewMessageRequest, moduleClient);

            await moduleClient.SetMethodDefaultHandlerAsync(DefaultMethodHandler, moduleClient);

            await moduleClient.SetMessageHandlerAsync(DefaultMessageHandler, moduleClient);

            // Wait until the app unloads or is cancelled
            _cts = new CancellationTokenSource();
            AssemblyLoadContext.Default.Unloading += (ctx) => _cts.Cancel();
            Console.CancelKeyPress += (sender, cpe) => _cts.Cancel();
            await WhenCancelled(_cts.Token);

            return 0;
        }

        private static Task<MessageResponse> DefaultMessageHandler(Message message, object userContext)
        {
            Log.Information($"Received new message on the DefaultMessageHandler!");
            Log.Information(Encoding.UTF8.GetString(message.GetBytes()));
            return Task.FromResult(MessageResponse.Completed);
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
        /// Default method handler for any method calls which are not implemented
        /// </summary>
        /// <param name="methodRequest"></param>
        /// <param name="userContext"></param>
        /// <returns></returns>
        private static Task<MethodResponse> DefaultMethodHandler(MethodRequest methodRequest, object userContext)
        {
            Log.Information($"Received method invocation for non-existing method {methodRequest.Name}. Returning 404.");
            var result = new MethodResponsePayload() { ModuleResponse = $"Method {methodRequest.Name} not implemented" };
            var outResult = JsonConvert.SerializeObject(result, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
            return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(outResult), 404));
        }

        /// <summary>
        /// Handler for NewMessageRequests
        /// Creates a new IoT Message based on the input from the method request and sends it to the Edge Hub
        /// </summary>
        /// <param name="methodRequest"></param>
        /// <param name="userContext"></param>
        /// <returns></returns>
        private static async Task<MethodResponse> NewMessageRequest(MethodRequest methodRequest, object userContext)
        {
            int counterValue = Interlocked.Increment(ref counter);
            var moduleClient = userContext as ModuleClient;

            int resultCode = 200;
            MethodResponsePayload result;

            var request = JsonConvert.DeserializeObject<MethodRequestPayload>(methodRequest.DataAsJson);

            if (!string.IsNullOrEmpty(request.CorrelationId))
            {
                var telemetryProperties = new Dictionary<string, string>
                {
                    { "correlationId", request.CorrelationId },
                    { "processingStep", "20-DirectMethodReceiverModule"},
                    { "edgeModuleId", Environment.GetEnvironmentVariable("IOTEDGE_MODULEID") }
                };

                Log.Information($"NewMessageRequest method invocation received. Count={counterValue}. CorrelationId={request.CorrelationId}");
                telemetry.TrackEvent("20-ReceivedDirectMethodRequest", telemetryProperties);

                var message = new Message(Encoding.UTF8.GetBytes("{\"text\": \"" + request.Text + "\"}"));
                message.ContentType = "application/json";
                message.ContentEncoding = "UTF-8";
                message.Properties.Add("correlationId", request.CorrelationId);
                message.Properties.Add("scope", "end2end");

                try
                {
                    await moduleClient.SendEventAsync("output1", message);
                    telemetry.TrackEvent("21-MessageSentToEdgeHub", telemetryProperties);
                    Log.Information("Message sent successfully to Edge Hub");
                    result = new MethodResponsePayload() { ModuleResponse = $"Message sent successfully to Edge Hub" };
                }
                catch (Exception e)
                {
                    Log.Error(e, "Error during message sending to Edge Hub");
                    telemetry.TrackEvent("25-ErrorMessageNotSentToEdgeHub", telemetryProperties);
                    resultCode = 500;
                    result = new MethodResponsePayload() { ModuleResponse = $"Message not sent to Edge Hub" };
                }
            }
            else
            {
                resultCode = 400;
                result = new MethodResponsePayload() { ModuleResponse = $"NewMessageRequest received without correlationId property" };
                Log.Warning("NewMessageRequest received without correlationId property");
            }
            var outResult = JsonConvert.SerializeObject(result);
            return new MethodResponse(Encoding.UTF8.GetBytes(outResult), resultCode);
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

    /// <summary>
    /// Payload of direct method request
    /// </summary>
    class MethodRequestPayload
    {
        public string CorrelationId { get; set; }
        public string Text { get; set; }
    }

    /// <summary>
    /// Payload of direct method response
    /// </summary>
    class MethodResponsePayload
    {
        public string ModuleResponse { get; set; } = null;
    }
}