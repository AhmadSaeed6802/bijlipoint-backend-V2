using BijliPoint.Data;
using BijliPoint.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace BijliPoint.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MqttController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IMemoryCache _cache;

        public MqttController(ApplicationDbContext context, IMemoryCache cache)
        {
            _context = context;
            _cache = cache;
        }

        // GET: api/mqtt/readings/{stationId}
        [HttpGet("readings/{stationId}")]
        public async Task<IActionResult> GetReadings(int stationId, [FromQuery] int limit = 100)
        {
            var readings = await _context.MeterReadings
                .Where(r => r.StationId == stationId)
                .OrderByDescending(r => r.Timestamp)
                .Take(limit)
                .Select(r => new
                {
                    r.PortNumber,
                    r.Timestamp,
                    r.Voltage,
                    r.Current,
                    r.Power,
                    r.Energy
                })
                .ToListAsync();

            return Ok(readings);
        }

        // GET: api/mqtt/latest/{stationId}
        [HttpGet("latest/{stationId}")]
        public async Task<IActionResult> GetLatestReadings(int stationId)
        {
            var results = new List<MeterReading>();

            // Try to get cached port list
            var portsKey = $"meter:ports:{stationId}";
            if (_cache.TryGetValue(portsKey, out HashSet<int> portNumbers))
            {
                foreach (var port in portNumbers)
                {
                    var cacheKey = $"meter:last:{stationId}:{port}";
                    if (_cache.TryGetValue(cacheKey, out MeterReading cached))
                        results.Add(cached);
                }
            }

            // Fallback to DB if cache empty (e.g. after service restart)
            if (results.Count == 0)
            {
                results = await _context.MeterReadings
                    .Where(r => r.StationId == stationId)
                    .GroupBy(r => r.PortNumber)
                    .Select(g => g.OrderByDescending(r => r.Timestamp).FirstOrDefault())
                    .ToListAsync();
            }

            // Never expose station or any sensitive fields — return meter data only
            var safe = results.Select(r => new
            {
                r.PortNumber,
                r.Voltage,
                r.Current,
                r.Power,
                r.Energy,
                ReceivedAt = DateTime.SpecifyKind(r.ReceivedAt, DateTimeKind.Utc)
            });

            return Ok(safe);
        }


        // POST: api/mqtt/control
        [HttpPost("control")]
        public async Task<IActionResult> ControlPort([FromBody] ControlRequest request)
        {
            // Validate request
            if (request.Command != "ON" && request.Command != "OFF")
                return BadRequest(new { error = "Command must be ON or OFF" });

            var station = await _context.Stations.FindAsync(request.StationId);
            if (station == null)
                return NotFound(new { error = "Station not found" });

            // Create command
            var command = new PortCommand
            {
                StationId = request.StationId,
                PortNumber = request.PortNumber,
                Command = request.Command,
                RequestedBy = request.UserId,
                RequestedAt = DateTime.UtcNow,
                Status = "Pending"
            };

            _context.PortCommands.Add(command);
            await _context.SaveChangesAsync();

            return Ok(new
            {
                commandId = command.Id,
                message = $"Command {request.Command} sent to Station {station.StationID}, Port {request.PortNumber:D2}"
            });
        }

        // GET: api/mqtt/port-status/{stationId}/{portNumber}
        [HttpGet("port-status/{stationId}/{portNumber}")]
        public async Task<IActionResult> GetPortStatus(int stationId, int portNumber)
        {
            var lastCommand = await _context.PortCommands
                .Where(c => c.StationId == stationId && c.PortNumber == portNumber && c.Status == "Executed")
                .OrderByDescending(c => c.ExecutedAt)
                .FirstOrDefaultAsync();

            return Ok(new
            {
                stationId,
                portNumber,
                status = lastCommand?.Command ?? "UNKNOWN",
                lastUpdated = lastCommand?.ExecutedAt
            });
        }

        // GET: api/mqtt/energy-summary/{stationId}
        [HttpGet("energy-summary/{stationId}")]
        public async Task<IActionResult> GetEnergySummary(int stationId, [FromQuery] DateTime? from, [FromQuery] DateTime? to)
        {
            var fromDate = from ?? DateTime.UtcNow.AddHours(-24);
            var toDate = to ?? DateTime.UtcNow;

            var summary = await _context.MeterReadings
                .Where(r => r.StationId == stationId && r.Timestamp >= fromDate && r.Timestamp <= toDate)
                .GroupBy(r => r.PortNumber)
                .Select(g => new
                {
                    portNumber = g.Key,
                    totalEnergy = g.Sum(r => r.Energy),
                    avgPower = g.Average(r => r.Power),
                    maxPower = g.Max(r => r.Power),
                    dataPoints = g.Count()
                })
                .ToListAsync();

            return Ok(new
            {
                stationId,
                period = new { from = fromDate, to = toDate },
                ports = summary
            });
        }
    }

    public class ControlRequest
    {
        public int StationId { get; set; }
        public int PortNumber { get; set; }
        public string Command { get; set; }
        public int UserId { get; set; }
    }
}
