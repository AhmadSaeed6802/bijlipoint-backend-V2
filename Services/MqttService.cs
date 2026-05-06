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
        private readonly IMemoryCache _cache;
        private IMqttClient _mqttClient;
        private MqttClientOptions _options;
        private MqttClientSubscribeOptions _subscribeOptions;

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

            _options = new MqttClientOptionsBuilder()
                .WithTcpServer(_config["Mqtt:Host"], int.Parse(_config["Mqtt:Port"]))
                .WithCredentials(_config["Mqtt:Username"], _config["Mqtt:Password"])
                .WithClientId($"{_config["Mqtt:ClientPrefix"] ?? "BP"}-{Environment.MachineName}")
                .WithCleanSession()
                .Build();

            _subscribeOptions = new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter("bijlipoint/stations/+/meter")
                .Build();

            _mqttClient.ApplicationMessageReceivedAsync += async e => await HandleMessage(e);

            // Auto-reconnect on any drop
            _mqttClient.DisconnectedAsync += async e =>
            {
                _logger.LogWarning("MQTT disconnected. Reconnecting in 5s...");
                await Task.Delay(5000);
                await ConnectAndSubscribe();
            };

            await ConnectAndSubscribe();

            int tick = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                await PublishPendingCommands();
                if (++tick % 30 == 0) await CheckSessionHealth(); // every 30 s
                await Task.Delay(1000, stoppingToken);
            }
        }

        private async Task ConnectAndSubscribe()
        {
            try
            {
                await _mqttClient.ConnectAsync(_options, CancellationToken.None);
                _logger.LogInformation("Connected to MQTT broker at {Host}:{Port}", _config["Mqtt:Host"], _config["Mqtt:Port"]);
                await _mqttClient.SubscribeAsync(_subscribeOptions, CancellationToken.None);
                _logger.LogInformation("Subscribed to: bijlipoint/stations/+/meter");
            }
            catch (Exception ex)
            {
                _logger.LogError("MQTT connect failed: {Msg}", ex.Message);
            }
        }

        private const decimal I_CHARGING = 0.3m; // CT clamp noise floor — same as frontend

        private async Task HandleMessage(MqttApplicationMessageReceivedEventArgs e)
        {
            try
            {
                var topic   = e.ApplicationMessage.Topic;
                var payload = System.Text.Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

                _logger.LogInformation("Received: {Topic} -> {Payload}", topic, payload);

                // Topic: bijlipoint/stations/{StationID16}{Port2}/meter  → segment = 18 chars
                var parts = topic.Split('/');
                if (parts.Length < 4) return;

                var seg = parts[2];
                if (seg.Length != 18)
                {
                    _logger.LogWarning("Unexpected topic segment length {Len}: {Seg}", seg.Length, seg);
                    return;
                }

                var stationID = seg.Substring(0, 16);
                var portStr   = seg.Substring(16, 2);

                if (!int.TryParse(portStr, out int portNumber) || portNumber < 1 || portNumber > 3)
                {
                    _logger.LogWarning("Invalid port in topic: {Port}", portStr);
                    return;
                }

                using var scope   = _serviceProvider.CreateScope();
                var context       = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var station = await context.Stations
                    .FirstOrDefaultAsync(s => s.StationID == stationID && s.ApprovalStatus == "Approved");

                if (station == null)
                {
                    _logger.LogWarning("Ignored: unregistered or unapproved station {StationID}", stationID);
                    return;
                }

                // Payload: Timestamp,Voltage,Current,Power,Energy
                var data = payload.Split(',');
                if (data.Length < 5) return;

                var reading = new MeterReading
                {
                    StationId  = station.Id,
                    PortNumber = portNumber,
                    Timestamp  = DateTime.SpecifyKind(DateTime.Parse(data[0]), DateTimeKind.Utc),
                    Voltage    = decimal.Parse(data[1]),
                    Current    = decimal.Parse(data[2]),
                    Power      = decimal.Parse(data[3]),
                    Energy     = decimal.Parse(data[4]),
                    ReceivedAt = DateTime.UtcNow
                };

                bool needsSave = false;

                if (ShouldStoreReading(reading))
                {
                    context.MeterReadings.Add(reading);
                    needsSave = true;
                    _logger.LogInformation("Stored: StationID {SID} Port {Port} - {A}A {W}W",
                        stationID, portNumber, reading.Current, reading.Power);
                }

                // Update port set in cache
                var portsKey = $"meter:ports:{reading.StationId}";
                var ports    = _cache.GetOrCreate(portsKey, _ => new HashSet<int>());
                ports.Add(reading.PortNumber);
                _cache.Set(portsKey, ports, TimeSpan.FromHours(1));

                // ── Real-time session management ──────────────────────────────────
                bool isIdle = reading.Current < I_CHARGING;

                if (isIdle)
                {
                    // Any single idle reading during an active session → end immediately.
                    // This ensures billing stops the instant the charger disconnects,
                    // and prevents another rider from continuing under the same session.
                    var active = await context.ChargingSessions
                        .FirstOrDefaultAsync(s => s.StationId == station.Id
                                               && s.PortNumber == portNumber
                                               && s.Status == "Active");

                    if (active != null)
                    {
                        decimal energy = Math.Max(0, reading.Energy - active.EnergyAtStart);
                        active.Status        = "AutoCompleted";
                        active.EndTime       = DateTime.UtcNow;
                        active.UnitsConsumed = energy;
                        active.TotalCost     = Math.Round(energy * station.RatePerKwh, 2);
                        _cache.Remove($"session:idle:{station.Id}:{portNumber}");

                        // Always send OFF on session end for physical relay assurance
                        context.PortCommands.Add(new PortCommand
                        {
                            StationId   = station.Id,
                            PortNumber  = portNumber,
                            Command     = "OFF",
                            RequestedBy = 0,
                            RequestedAt = DateTime.UtcNow,
                            Status      = "Pending"
                        });
                        needsSave = true;
                        _logger.LogInformation("Session {Id} → AutoCompleted (idle reading, Port {P}, {E} kWh)",
                            active.Id, portNumber, energy);
                    }
                }
                else
                {
                    // Non-idle: check if a rider clicked Start and is waiting for this signal
                    var pendingKey = $"pending:start:{station.Id}:{portNumber}";
                    if (_cache.TryGetValue(pendingKey, out PendingSessionData pending))
                    {
                        _cache.Remove(pendingKey);
                        context.ChargingSessions.Add(new ChargingSession
                        {
                            RiderId       = pending.RiderId,
                            StationId     = station.Id,
                            PortNumber    = portNumber,
                            StartTime     = DateTime.UtcNow,
                            EnergyAtStart = reading.Energy,
                            UnitsConsumed = 0,
                            TotalCost     = 0,
                            Status        = "Active"
                        });
                        needsSave = true;
                        _logger.LogInformation("Session created from pending: Rider {R} Station {S} Port {P} @ {E} kWh",
                            pending.RiderId, station.Id, portNumber, reading.Energy);
                    }
                    else
                    {
                        // Charging detected with no active session → unauthorized protection.
                        // Rate-limited to one OFF per minute per port to avoid command spam.
                        var unauthKey = $"unauth:off:{station.Id}:{portNumber}";
                        if (!_cache.TryGetValue(unauthKey, out _))
                        {
                            bool hasSession = await context.ChargingSessions
                                .AnyAsync(s => s.StationId == station.Id
                                           && s.PortNumber == portNumber
                                           && s.Status == "Active");

                            if (!hasSession)
                            {
                                _cache.Set(unauthKey, true, TimeSpan.FromMinutes(1));
                                context.PortCommands.Add(new PortCommand
                                {
                                    StationId   = station.Id,
                                    PortNumber  = portNumber,
                                    Command     = "OFF",
                                    RequestedBy = 0,
                                    RequestedAt = DateTime.UtcNow,
                                    Status      = "Pending"
                                });
                                needsSave = true;
                                _logger.LogWarning("Unauthorized charging: Station {SId} Port {P} → OFF queued",
                                    station.Id, portNumber);
                            }
                        }
                    }
                }

                if (needsSave) await context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError("Error handling message: {Msg}", ex.Message);
            }
        }

        private bool ShouldStoreReading(MeterReading reading)
        {
            var cacheKey = $"meter:last:{reading.StationId}:{reading.PortNumber}";

            if (_cache.TryGetValue(cacheKey, out MeterReading last))
            {
                bool isIdle  = reading.Current <= 0.01m && reading.Power <= 0.5m;
                bool wasIdle = last.Current    <= 0.01m && last.Power    <= 0.5m;

                if (isIdle && wasIdle &&
                    Math.Abs(reading.Voltage - last.Voltage) <= 5 &&
                    reading.Energy == last.Energy)
                {
                    // Keep ReceivedAt fresh so stale detection stays accurate
                    last.ReceivedAt = reading.ReceivedAt;
                    _cache.Set(cacheKey, last, TimeSpan.FromMinutes(10));
                    return false;
                }
            }

            _cache.Set(cacheKey, reading, TimeSpan.FromMinutes(10));
            return true;
        }

        private async Task PublishPendingCommands()
        {
            if (!_mqttClient.IsConnected) return;
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context     = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var pending = await context.PortCommands
                    .Include(c => c.Station)
                    .Where(c => c.Status == "Pending")
                    .OrderBy(c => c.RequestedAt)
                    .ToListAsync();

                foreach (var cmd in pending)
                {
                    var topic = $"bijlipoint/stations/{cmd.Station.StationID}{cmd.PortNumber:D2}/control";

                    var message = new MqttApplicationMessageBuilder()
                        .WithTopic(topic)
                        .WithPayload(cmd.Command)
                        .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                        .Build();

                    await _mqttClient.PublishAsync(message);

                    cmd.Status    = "Executed";
                    cmd.ExecutedAt = DateTime.UtcNow;
                    await context.SaveChangesAsync();

                    _logger.LogInformation("Published command: {Topic} -> {Cmd}", topic, cmd.Command);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Error publishing commands: {Msg}", ex.Message);
            }
        }

        private async Task CheckSessionHealth()
        {
            try
            {
                using var scope   = _serviceProvider.CreateScope();
                var context       = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var active = await context.ChargingSessions
                    .Where(s => s.Status == "Active")
                    .ToListAsync();

                if (!active.Any()) return;

                bool changed = false;

                foreach (var session in active)
                {
                    var cacheKey = $"meter:last:{session.StationId}:{session.PortNumber}";
                    _cache.TryGetValue(cacheKey, out MeterReading last);

                    // ── Stale: no signal for > 2 minutes ──────────────────────────────
                    if (last == null || (DateTime.UtcNow - last.ReceivedAt).TotalMinutes > 2)
                    {
                        var station = await context.Stations.FindAsync(session.StationId);
                        decimal energy = last != null ? Math.Max(0, last.Energy - session.EnergyAtStart) : 0;
                        session.Status        = "Timeout";
                        session.EndTime       = DateTime.UtcNow;
                        session.UnitsConsumed = energy;
                        session.TotalCost     = Math.Round(energy * (station?.RatePerKwh ?? 18m), 2);
                        context.PortCommands.Add(new PortCommand
                        {
                            StationId   = session.StationId,
                            PortNumber  = session.PortNumber,
                            Command     = "OFF",
                            RequestedBy = 0,
                            RequestedAt = DateTime.UtcNow,
                            Status      = "Pending"
                        });
                        changed = true;
                        _logger.LogWarning("Session {Id} → Timeout (no signal >2min)", session.Id);
                        continue;
                    }

                    // ── Idle catch-all: safety net for sessions started before service restart.
                    // HandleMessage ends sessions immediately on idle readings; this catches
                    // any that slipped through (e.g. service restarted mid-session).
                    if (last.Current < I_CHARGING)
                    {
                        var station = await context.Stations.FindAsync(session.StationId);
                        decimal energy = Math.Max(0, last.Energy - session.EnergyAtStart);
                        session.Status        = "AutoCompleted";
                        session.EndTime       = DateTime.UtcNow;
                        session.UnitsConsumed = energy;
                        session.TotalCost     = Math.Round(energy * (station?.RatePerKwh ?? 18m), 2);
                        context.PortCommands.Add(new PortCommand
                        {
                            StationId   = session.StationId,
                            PortNumber  = session.PortNumber,
                            Command     = "OFF",
                            RequestedBy = 0,
                            RequestedAt = DateTime.UtcNow,
                            Status      = "Pending"
                        });
                        changed = true;
                        _logger.LogInformation("Session {Id} → AutoCompleted (idle catch-all, Port {P}, {E} kWh)",
                            session.Id, session.PortNumber, energy);
                    }
                }

                if (changed) await context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError("CheckSessionHealth error: {Msg}", ex.Message);
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_mqttClient?.IsConnected == true)
            {
                await _mqttClient.DisconnectAsync();
                _logger.LogInformation("Disconnected from MQTT broker");
            }
            await base.StopAsync(cancellationToken);
        }
    }
}
