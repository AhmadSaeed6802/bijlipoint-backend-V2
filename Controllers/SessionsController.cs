using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using BijliPoint.Data;
using BijliPoint.Models;

namespace BijliPoint.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SessionsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IMemoryCache _cache;

        public SessionsController(ApplicationDbContext context, IMemoryCache cache)
        {
            _context = context;
            _cache = cache;
        }

        // POST: api/sessions/start
        [HttpPost("start")]
        public async Task<IActionResult> StartSession([FromBody] StartSessionRequest request)
        {
            // Block if rider already has an active session
            var alreadyActive = await _context.ChargingSessions
                .AnyAsync(s => s.RiderId == request.RiderId && s.Status == "Active");
            if (alreadyActive)
                return BadRequest(new { error = "You already have an active charging session." });

            // Block if port is already in use
            var portBusy = await _context.ChargingSessions
                .AnyAsync(s => s.StationId == request.StationId && s.PortNumber == request.PortNumber && s.Status == "Active");
            if (portBusy)
                return BadRequest(new { error = "This port is already in use." });

            var station = await _context.Stations.FindAsync(request.StationId);
            if (station == null)
                return NotFound(new { error = "Station not found." });

            // Snapshot current meter energy so we can calculate delta on stop
            decimal energyAtStart = 0;
            var cacheKey = $"meter:last:{request.StationId}:{request.PortNumber}";
            if (_cache.TryGetValue(cacheKey, out MeterReading meterNow))
                energyAtStart = meterNow.Energy;

            var session = new ChargingSession
            {
                RiderId     = request.RiderId,
                StationId   = request.StationId,
                PortNumber  = request.PortNumber,
                StartTime   = DateTime.UtcNow,
                EnergyAtStart = energyAtStart,
                UnitsConsumed = 0,
                TotalCost   = 0,
                Status      = "Active"
            };
            _context.ChargingSessions.Add(session);

            // Queue ON command via MQTT
            _context.PortCommands.Add(new PortCommand
            {
                StationId   = request.StationId,
                PortNumber  = request.PortNumber,
                Command     = "ON",
                RequestedBy = request.RiderId,
                RequestedAt = DateTime.UtcNow,
                Status      = "Pending"
            });

            await _context.SaveChangesAsync();

            return Ok(new { sessionId = session.Id, message = "Charging started." });
        }

        // POST: api/sessions/stop/{sessionId}
        [HttpPost("stop/{sessionId}")]
        public async Task<IActionResult> StopSession(int sessionId)
        {
            var session = await _context.ChargingSessions.FindAsync(sessionId);
            if (session == null)
                return NotFound(new { error = "Session not found." });
            if (session.Status != "Active")
                return BadRequest(new { error = "Session is not active." });

            var station = await _context.Stations.FindAsync(session.StationId);

            // Calculate units from meter delta
            decimal currentEnergy = session.EnergyAtStart;
            var cacheKey = $"meter:last:{session.StationId}:{session.PortNumber}";
            if (_cache.TryGetValue(cacheKey, out MeterReading meterNow))
                currentEnergy = meterNow.Energy;

            // Guard against meter reset
            decimal units = Math.Max(0, currentEnergy - session.EnergyAtStart);
            decimal rate  = station?.RatePerKwh ?? 18m;

            session.EndTime       = DateTime.UtcNow;
            session.UnitsConsumed = units;
            session.TotalCost     = Math.Round(units * rate, 2);
            session.Status        = "Completed";

            // Queue OFF command via MQTT
            _context.PortCommands.Add(new PortCommand
            {
                StationId   = session.StationId,
                PortNumber  = session.PortNumber,
                Command     = "OFF",
                RequestedBy = session.RiderId,
                RequestedAt = DateTime.UtcNow,
                Status      = "Pending"
            });

            await _context.SaveChangesAsync();

            var duration = (session.EndTime.Value - session.StartTime).TotalMinutes;

            return Ok(new
            {
                sessionId     = session.Id,
                unitsConsumed = units,
                totalCost     = session.TotalCost,
                duration      = Math.Round(duration, 1),
                ratePerKwh    = rate,
                stationName   = station?.Name
            });
        }

        // GET: api/sessions/active/{riderId}
        [HttpGet("active/{riderId}")]
        public async Task<IActionResult> GetActiveSession(int riderId)
        {
            var session = await _context.ChargingSessions
                .Where(s => s.RiderId == riderId && s.Status == "Active")
                .FirstOrDefaultAsync();

            if (session == null)
                return Ok(null);

            var station = await _context.Stations.FindAsync(session.StationId);

            // Live meter from cache
            decimal currentEnergy = session.EnergyAtStart;
            decimal currentPower  = 0;
            decimal voltage       = 0;
            decimal current       = 0;

            var cacheKey = $"meter:last:{session.StationId}:{session.PortNumber}";
            if (_cache.TryGetValue(cacheKey, out MeterReading meterNow))
            {
                currentEnergy = meterNow.Energy;
                currentPower  = meterNow.Power;
                voltage       = meterNow.Voltage;
                current       = meterNow.Current;
            }

            decimal unitsNow = Math.Max(0, currentEnergy - session.EnergyAtStart);
            decimal rate     = station?.RatePerKwh ?? 18m;
            double  duration = (DateTime.UtcNow - session.StartTime).TotalMinutes;

            return Ok(new
            {
                sessionId     = session.Id,
                stationId     = session.StationId,
                stationName   = station?.Name,
                portNumber    = session.PortNumber,
                startTime     = session.StartTime,
                durationMin   = Math.Round(duration, 1),
                unitsConsumed = unitsNow,
                estimatedCost = Math.Round(unitsNow * rate, 2),
                ratePerKwh    = rate,
                power         = currentPower,
                voltage       = voltage,
                current       = current
            });
        }

        // POST: api/sessions/force-stop/{sessionId}  — CNO emergency stop
        [HttpPost("force-stop/{sessionId}")]
        public async Task<IActionResult> ForceStop(int sessionId)
        {
            var session = await _context.ChargingSessions.FindAsync(sessionId);
            if (session == null) return NotFound(new { error = "Session not found." });
            if (session.Status != "Active") return BadRequest(new { error = "Session is not active." });

            var station = await _context.Stations.FindAsync(session.StationId);
            decimal currentEnergy = session.EnergyAtStart;
            var cacheKey = $"meter:last:{session.StationId}:{session.PortNumber}";
            if (_cache.TryGetValue(cacheKey, out MeterReading meterNow))
                currentEnergy = meterNow.Energy;

            decimal units = Math.Max(0, currentEnergy - session.EnergyAtStart);
            decimal rate  = station?.RatePerKwh ?? 18m;

            session.EndTime       = DateTime.UtcNow;
            session.UnitsConsumed = units;
            session.TotalCost     = Math.Round(units * rate, 2);
            session.Status        = "ForceStopped";

            _context.PortCommands.Add(new PortCommand
            {
                StationId   = session.StationId,
                PortNumber  = session.PortNumber,
                Command     = "OFF",
                RequestedBy = 0,
                RequestedAt = DateTime.UtcNow,
                Status      = "Pending"
            });

            await _context.SaveChangesAsync();
            return Ok(new { message = "Session force-stopped.", units, totalCost = session.TotalCost });
        }

        // GET: api/sessions/history/{riderId}
        [HttpGet("history/{riderId}")]
        public async Task<IActionResult> GetHistory(int riderId)
        {
            var sessions = await _context.ChargingSessions
                .Where(s => s.RiderId == riderId && s.Status != "Active")
                .OrderByDescending(s => s.StartTime)
                .ToListAsync();

            var stationIds = sessions.Select(s => s.StationId).Distinct().ToList();
            var stations   = await _context.Stations
                .Where(s => stationIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Name);

            var result = sessions.Select(s => new
            {
                s.Id,
                s.StationId,
                stationName   = stations.GetValueOrDefault(s.StationId, "Unknown"),
                s.PortNumber,
                s.StartTime,
                s.EndTime,
                durationMin   = s.EndTime.HasValue
                    ? Math.Round((s.EndTime.Value - s.StartTime).TotalMinutes, 1)
                    : 0,
                s.UnitsConsumed,
                s.TotalCost,
                s.Status
            });

            return Ok(result);
        }

        // GET: api/sessions/station/{stationId}  — CNO revenue view
        [HttpGet("station/{stationId}")]
        public async Task<IActionResult> GetStationSessions(int stationId, [FromQuery] int days = 30)
        {
            try
            {
                var from = DateTime.UtcNow.AddDays(-days);

                var sessions = await _context.ChargingSessions
                    .Where(s => s.StationId == stationId && s.StartTime >= from)
                    .OrderByDescending(s => s.StartTime)
                    .ToListAsync();

                // Build rider name lookup — fall back to "Unknown" if user missing
                Dictionary<int, string> riders = new();
                if (sessions.Count > 0)
                {
                    var riderIds = sessions.Select(s => s.RiderId).Distinct().ToList();
                    riders = await _context.Users
                        .Where(u => riderIds.Contains(u.Id))
                        .ToDictionaryAsync(u => u.Id, u => u.Name ?? "Unknown");
                }

                var result = sessions.Select(s => new
                {
                    s.Id,
                    s.PortNumber,
                    riderName     = riders.GetValueOrDefault(s.RiderId, "Unknown"),
                    s.StartTime,
                    s.EndTime,
                    durationMin   = s.EndTime.HasValue
                        ? Math.Round((s.EndTime.Value - s.StartTime).TotalMinutes, 1)
                        : Math.Round((DateTime.UtcNow - s.StartTime).TotalMinutes, 1),
                    s.UnitsConsumed,
                    s.TotalCost,
                    s.Status
                }).ToList(); // force evaluation before serialization

                var ended = sessions.Where(s => s.Status != "Active").ToList();

                return Ok(new
                {
                    totalSessions  = ended.Count,
                    totalRevenue   = ended.Sum(s => s.TotalCost),
                    totalUnits     = ended.Sum(s => s.UnitsConsumed),
                    activeSessions = sessions.Count(s => s.Status == "Active"),
                    sessions       = result
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // GET: api/sessions/station/{stationId}/port-health?days=7  — CNO port health
        [HttpGet("station/{stationId}/port-health")]
        public async Task<IActionResult> GetStationPortHealth(int stationId, [FromQuery] int days = 7)
        {
            var from = DateTime.UtcNow.AddDays(-days);

            var flagged = await _context.ChargingSessions
                .Where(s => s.StationId == stationId && s.Status == "Timeout" && s.StartTime >= from)
                .GroupBy(s => s.PortNumber)
                .Select(g => new { PortNumber = g.Key, timeoutCount = g.Count() })
                .Where(g => g.timeoutCount >= 2)
                .OrderByDescending(g => g.timeoutCount)
                .ToListAsync();

            return Ok(flagged.Select(f => new
            {
                f.PortNumber,
                f.timeoutCount,
                severity = f.timeoutCount >= 5 ? "High" : f.timeoutCount >= 3 ? "Medium" : "Low"
            }));
        }

        // GET: api/sessions/admin/all?days=30&status=  — super admin all stations
        [HttpGet("admin/all")]
        public async Task<IActionResult> GetAdminSessions([FromQuery] int days = 30, [FromQuery] string status = "")
        {
            var from = DateTime.UtcNow.AddDays(-days);

            var query = _context.ChargingSessions.Where(s => s.StartTime >= from);
            if (!string.IsNullOrEmpty(status)) query = query.Where(s => s.Status == status);

            var sessions = await query.OrderByDescending(s => s.StartTime).ToListAsync();

            var stationIds = sessions.Select(s => s.StationId).Distinct().ToList();
            var riderIds   = sessions.Select(s => s.RiderId).Distinct().ToList();

            var stations = await _context.Stations
                .Where(s => stationIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Name);
            var riders = await _context.Users
                .Where(u => riderIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.Name);

            var result = sessions.Select(s => new
            {
                s.Id,
                s.StationId,
                stationName  = stations.GetValueOrDefault(s.StationId, "Unknown"),
                s.PortNumber,
                riderName    = riders.GetValueOrDefault(s.RiderId, "Unknown"),
                s.StartTime,
                s.EndTime,
                durationMin  = s.EndTime.HasValue
                    ? Math.Round((s.EndTime.Value - s.StartTime).TotalMinutes, 1)
                    : Math.Round((DateTime.UtcNow - s.StartTime).TotalMinutes, 1),
                s.UnitsConsumed,
                s.TotalCost,
                s.Status
            });

            var closed = sessions.Where(s => s.Status != "Active").ToList();
            return Ok(new
            {
                totalSessions    = closed.Count,
                totalRevenue     = closed.Sum(s => s.TotalCost),
                totalUnits       = closed.Sum(s => s.UnitsConsumed),
                activeSessions   = sessions.Count(s => s.Status == "Active"),
                timeoutSessions  = sessions.Count(s => s.Status == "Timeout"),
                sessions         = result
            });
        }

        // GET: api/sessions/admin/port-health?days=7  — ports with repeated timeouts
        [HttpGet("admin/port-health")]
        public async Task<IActionResult> GetPortHealth([FromQuery] int days = 7)
        {
            var from = DateTime.UtcNow.AddDays(-days);

            var flagged = await _context.ChargingSessions
                .Where(s => s.Status == "Timeout" && s.StartTime >= from)
                .GroupBy(s => new { s.StationId, s.PortNumber })
                .Select(g => new { g.Key.StationId, g.Key.PortNumber, timeoutCount = g.Count() })
                .Where(g => g.timeoutCount >= 2)
                .OrderByDescending(g => g.timeoutCount)
                .ToListAsync();

            var stationIds = flagged.Select(f => f.StationId).Distinct().ToList();
            var stations   = await _context.Stations
                .Where(s => stationIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => new { s.Name, s.StationID });

            return Ok(flagged.Select(f => new
            {
                f.StationId,
                stationName  = stations.GetValueOrDefault(f.StationId)?.Name    ?? "Unknown",
                stationID    = stations.GetValueOrDefault(f.StationId)?.StationID ?? "",
                f.PortNumber,
                f.timeoutCount,
                severity     = f.timeoutCount >= 5 ? "High" : f.timeoutCount >= 3 ? "Medium" : "Low"
            }));
        }

        // GET: api/sessions/user/{userId}  — kept for backward compat
        [HttpGet("user/{userId}")]
        public async Task<IActionResult> GetUserSessions(int userId)
            => await GetHistory(userId);
    }

    public class StartSessionRequest
    {
        public int RiderId    { get; set; }
        public int StationId  { get; set; }
        public int PortNumber { get; set; }
    }
}
