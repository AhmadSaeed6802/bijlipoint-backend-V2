using MQTTnet;
using MQTTnet.Client;
using Microsoft.EntityFrameworkCore;
using BijliPoint.Data;
using BijliPoint.Models;
using Microsoft.Extensions.Caching.Memory;

namespace BijliPoint.Services
{
    public class MqttService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<MqttService> _logger;
        private readonly IConfiguration _config;
        private IMqttClient _mqttClient;
        private readonly IMemoryCache _cache;


        public MqttService(IServiceProvider serviceProvider, ILogger<MqttService> logger, IConfiguration config, IMemoryCache cache)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _config = config;
            _cache = cache;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var factory = new MqttFactory();
            _mqttClient = factory.CreateMqttClient();

            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(_config["Mqtt:Host"], int.Parse(_config["Mqtt:Port"])) // Mosquitto on same VPS
                .WithCredentials(_config["Mqtt:Username"], _config["Mqtt:Password"])
                .WithClientId("BijliPointBackend")
                .WithCleanSession()
                .Build();

            // Handle incoming messages
            _mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                await HandleMessage(e);
            };

            // Connect
            await _mqttClient.ConnectAsync(options, stoppingToken);
            _logger.LogInformation("Connected to MQTT broker at {Host}:{Port}", _config["Mqtt:Host"], _config["Mqtt:Port"]);

            // Subscribe to all station topics
            var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter("bijlipoint/stations/+/meter")
                .Build();
            await _mqttClient.SubscribeAsync(subscribeOptions, stoppingToken);
            _logger.LogInformation("Subscribed to meter data topics");

            // Keep running and publish control commands
            while (!stoppingToken.IsCancellationRequested)
            {
                await PublishPendingCommands();
                await Task.Delay(1000, stoppingToken); // Check every second
            }
        }

        private async Task HandleMessage(MqttApplicationMessageReceivedEventArgs e)
        {
            try
            {
                var topic = e.ApplicationMessage.Topic;
                var payload = System.Text.Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

                _logger.LogInformation($"Received: {topic} -> {payload}");

                // Topic format: bijlipoint/stations/{stationId}/meter
                var parts = topic.Split('/');
                if (parts.Length < 4) return;

                var stationIdStr = parts[2];
                if (!int.TryParse(stationIdStr, out int stationId)) return;

                // Payload format: <Timestamp,PortNumber,V,I,P,Energy>
                // Example: 2026-03-28T12:34:56,1,220.5,10.2,2250.0,1.5
                var data = payload.Split(',');
                if (data.Length < 6) return;

                var reading = new MeterReading
                {
                    StationId = stationId,
                    PortNumber = int.Parse(data[1]),
                    Timestamp = DateTime.Parse(data[0]),
                    Voltage = decimal.Parse(data[2]),
                    Current = decimal.Parse(data[3]),
                    Power = decimal.Parse(data[4]),
                    Energy = decimal.Parse(data[5]),
                    ReceivedAt = DateTime.UtcNow
                };

                // Store in database
                // SMART STORAGE: Skip duplicate idle data
                if (ShouldStoreReading(reading))
                {
                    using var scope = _serviceProvider.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    context.MeterReadings.Add(reading);
                    await context.SaveChangesAsync();

                    _logger.LogInformation("Stored: Station {0} Port {1} - {2}A {3}W",
                        stationId, reading.PortNumber, reading.Current, reading.Power);
                }
                else
                {
                    _logger.LogDebug("Skipped idle: Station {0} Port {1}", stationId, reading.PortNumber);
                }


                // Maintain cached list of ports for this station
                var portsKey = $"meter:ports:{reading.StationId}";
                var ports = _cache.GetOrCreate(portsKey, entry => new HashSet<int>());
                ports.Add(reading.PortNumber);
                _cache.Set(portsKey, ports, TimeSpan.FromHours(1));
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error handling message: {ex.Message}");
            }
        }

        private bool ShouldStoreReading(MeterReading reading)
        {
           
            var cacheKey = $"meter:last:{reading.StationId}:{reading.PortNumber}";


            // Get last stored reading from cache
            if (_cache.TryGetValue(cacheKey, out MeterReading lastReading))
            {
                // Is this reading idle? (no current flowing) with tolerance
                bool isIdle = reading.Current <= 0.01m && reading.Power <= 0.5m;

                // Was last reading also idle?
                bool wasIdle = lastReading.Current <= 0.01m && lastReading.Power <= 0.5m;

                // Skip if: both idle AND values unchanged (within tolerance)
                if (isIdle && wasIdle &&
                     Math.Abs(reading.Voltage - lastReading.Voltage) <= 5 &&
                    reading.Energy == lastReading.Energy)
                {
                    return false; // Skip duplicate idle data
                }
            }

            // Store this reading in cache for next comparison
            _cache.Set(cacheKey, reading, TimeSpan.FromMinutes(10));

            return true; // Store to database
        }

        private async Task PublishPendingCommands()
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var pending = await context.PortCommands
                    .Where(c => c.Status == "Pending")
                    .OrderBy(c => c.RequestedAt)
                    .ToListAsync();

                foreach (var cmd in pending)
                {
                    // Topic: bijlipoint/stations/{stationId}/control/{portNumber}
                    var topic = $"bijlipoint/stations/{cmd.StationId}/control/{cmd.PortNumber}";
                    var payload = cmd.Command; // "ON" or "OFF"

                    var message = new MqttApplicationMessageBuilder()
                        .WithTopic(topic)
                        .WithPayload(payload)
                        .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                        .Build();

                    await _mqttClient.PublishAsync(message);

                    cmd.Status = "Executed";
                    cmd.ExecutedAt = DateTime.UtcNow;
                    await context.SaveChangesAsync();

                    _logger.LogInformation($"Published command: {topic} -> {payload}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error publishing commands: {ex.Message}");
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_mqttClient != null)
            {
                await _mqttClient.DisconnectAsync();
                _logger.LogInformation("Disconnected from MQTT broker");
            }
            await base.StopAsync(cancellationToken);
        }
    }
}
