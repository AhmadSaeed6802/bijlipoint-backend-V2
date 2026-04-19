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

            // ✅ UPDATED: Subscribe using StationID pattern
            var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter("bijlipoint/stations/+/meter")  // + matches any StationID
                .Build();
            await _mqttClient.SubscribeAsync(subscribeOptions, stoppingToken);
            _logger.LogInformation("Subscribed to: bijlipoint/stations/+/meter");

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

                // Topic format: bijlipoint/stations/{StationID}{PortPadded}/meter
                // Example port 01: bijlipoint/stations/LUMS99999999999901/meter
                // Example port 02: bijlipoint/stations/LUMS99999999999902/meter
                var parts = topic.Split('/');
                if (parts.Length < 4) return;

                var topicSegment = parts[2]; // e.g. "LUMS99999999999901"

                // Must be 18 chars: 16 StationID + 2 port digits
                if (topicSegment.Length != 18)
                {
                    _logger.LogWarning("Unexpected topic segment length {Len}: {Seg}", topicSegment.Length, topicSegment);
                    return;
                }

                var stationID = topicSegment.Substring(0, 16); // "LUMS999999999999"
                var portStr   = topicSegment.Substring(16, 2); // "01"

                if (!int.TryParse(portStr, out int portNumber) || portNumber < 1 || portNumber > 3)
                {
                    _logger.LogWarning("Invalid port in topic: {Port}", portStr);
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var station = await context.Stations
                    .FirstOrDefaultAsync(s => s.StationID == stationID && s.ApprovalStatus == "Approved");

                if (station == null)
                {
                    _logger.LogWarning("Ignored: unregistered or unapproved station {StationID}", stationID);
                    return;
                }

                // Payload format: Timestamp,Voltage,Current,Power,Energy  (port is now in topic)
                // Example: 2026-04-20T12:00:00,220.5,10.2,2250.0,1.5
                var data = payload.Split(',');
                if (data.Length < 5) return;

                var reading = new MeterReading
                {
                    StationId  = station.Id,
                    PortNumber = portNumber,
                    Timestamp  = DateTime.Parse(data[0]),
                    Voltage    = decimal.Parse(data[1]),
                    Current    = decimal.Parse(data[2]),
                    Power      = decimal.Parse(data[3]),
                    Energy     = decimal.Parse(data[4]),
                    ReceivedAt = DateTime.UtcNow
                };

                if (ShouldStoreReading(reading))
                {
                    context.MeterReadings.Add(reading);
                    await context.SaveChangesAsync();

                    _logger.LogInformation("Stored: StationID {0} (DB ID {1}) Port {2} - {3}A {4}W",
                        stationID, station.Id, reading.PortNumber, reading.Current, reading.Power);
                }

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
                    // Still refresh ReceivedAt so stale detection knows hardware is alive
                    lastReading.ReceivedAt = reading.ReceivedAt;
                    _cache.Set(cacheKey, lastReading, TimeSpan.FromMinutes(10));
                    return false; // Skip duplicate idle data — don't write to DB
                }
            }

            _cache.Set(cacheKey, reading, TimeSpan.FromMinutes(10));
            return true;
        }

        private async Task PublishPendingCommands()
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var pending = await context.PortCommands
                    .Include(c => c.Station)  // ✅ Include Station to get StationID
                    .Where(c => c.Status == "Pending")
                    .OrderBy(c => c.RequestedAt)
                    .ToListAsync();

                foreach (var cmd in pending)
                {
                    // Control topic: bijlipoint/stations/{StationID}{PortPadded}/control
                    // Example: bijlipoint/stations/LUMS99999999999901/control
                    var paddedPort = cmd.PortNumber.ToString("D2");
                    var topic = $"bijlipoint/stations/{cmd.Station.StationID}{paddedPort}/control";
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
