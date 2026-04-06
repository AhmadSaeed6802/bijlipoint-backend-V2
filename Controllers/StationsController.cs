using BijliPoint.Data;
using BijliPoint.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BijliPoint.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class StationsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public StationsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // POST: api/stations/register
        [HttpPost("register")]
        public async Task<IActionResult> RegisterStation([FromBody] StationRegistrationRequest request)
        {
            // Validate
            if (string.IsNullOrEmpty(request.StationPassword) || request.StationPassword.Length < 6)
                return BadRequest(new { error = "Station password must be at least 6 characters" });

            var station = new Station
            {
                OwnerId = request.OwnerId,
                Name = request.Name,
                Latitude = request.Latitude,
                Longitude = request.Longitude,
                Address = request.Address,
                RatePerKwh = request.RatePerKwh,
                TotalPlugs = request.TotalPlugs,
                AvailablePlugs = request.TotalPlugs,
                IsOpen = true,
                OpenTime = string.IsNullOrEmpty(request.OpenTime)
                    ? TimeSpan.Parse("00:00")
                    : TimeSpan.Parse(request.OpenTime),

                CloseTime = string.IsNullOrEmpty(request.CloseTime)
                    ? TimeSpan.Parse("23:59")
                    : TimeSpan.Parse(request.CloseTime),
                //OpenTime = request.OpenTime ?? "00:00",
                //CloseTime = request.CloseTime ?? "23:59",
                WhatsAppNumber = request.WhatsAppNumber,
                IsActive = false, // Inactive until approved
                StationPassword = BCrypt.Net.BCrypt.HashPassword(request.StationPassword),
                ApprovalStatus = "Pending",
                CreatedAt = DateTime.UtcNow
            };

            _context.Stations.Add(station);
            await _context.SaveChangesAsync();

            return Ok(new
            {
                stationId = station.Id,
                message = "Station registration submitted. Waiting for admin approval."
            });
        }

        // GET: api/stations/my-stations/{ownerId}
        [HttpGet("my-stations/{ownerId}")]
        public async Task<IActionResult> GetMyStations(int ownerId)
        {
            var stations = await _context.Stations
                .Where(s => s.OwnerId == ownerId)
                .Select(s => new
                {
                    s.Id,
                    s.Name,
                    s.Address,
                    s.TotalPlugs,
                    s.RatePerKwh,
                    s.ApprovalStatus,
                    s.IsActive,
                    s.CreatedAt
                })
                .OrderByDescending(s => s.CreatedAt)
                .ToListAsync();

            return Ok(stations);
        }

        // POST: api/stations/verify-access
        [HttpPost("verify-access")]
        public async Task<IActionResult> VerifyStationAccess([FromBody] StationAccessRequest request)
        {
            var station = await _context.Stations.FindAsync(request.StationId);
            
            if (station == null)
                return NotFound(new { error = "Station not found" });

            if (station.ApprovalStatus != "Approved")
                return BadRequest(new { error = "Station not approved yet" });

            if (!BCrypt.Net.BCrypt.Verify(request.Password, station.StationPassword))
                return Unauthorized(new { error = "Invalid station password" });

            return Ok(new
            {
                stationId = station.Id,
                name = station.Name,
                totalPlugs = station.TotalPlugs,
                message = "Access granted"
            });
        }

        // GET: api/stations/pending (Admin only)
        [HttpGet("pending")]
        public async Task<IActionResult> GetPendingStations()
        {
            var pending = await _context.Stations
                .Where(s => s.ApprovalStatus == "Pending")
                //.Include(s => s.Name) // Optional: include owner details
                .Select(s => new
                {
                    s.Id,
                    s.Name,
                    s.Address,
                    s.TotalPlugs,
                    s.RatePerKwh,
                    s.OwnerId,
                    s.CreatedAt
                })
                .OrderBy(s => s.CreatedAt)
                .ToListAsync();

            return Ok(pending);
        }

        // POST: api/stations/approve
        [HttpPost("approve")]
        public async Task<IActionResult> ApproveStation([FromBody] ApprovalRequest request)
        {
            var station = await _context.Stations.FindAsync(request.StationId);
            
            if (station == null)
                return NotFound(new { error = "Station not found" });

            station.ApprovalStatus = request.Approve ? "Approved" : "Rejected";
            station.IsActive = request.Approve;
            station.ApprovedBy = request.AdminId;
            station.ApprovedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return Ok(new
            {
                message = request.Approve ? "Station approved" : "Station rejected",
                stationId = station.Id
            });
        }

        // GET: api/stations/details/{stationId}
        [HttpGet("details/{stationId}")]
        public async Task<IActionResult> GetStationDetails(int stationId)
        {
            var station = await _context.Stations.FindAsync(stationId);
            
            if (station == null)
                return NotFound(new { error = "Station not found" });

            return Ok(new
            {
                station.Id,
                station.Name,
                station.Address,
                station.Latitude,
                station.Longitude,
                station.TotalPlugs,
                station.AvailablePlugs,
                station.RatePerKwh,
                station.IsOpen,
                station.OpenTime,
                station.CloseTime,
                station.WhatsAppNumber,
                station.ApprovalStatus,
                station.IsActive
            });
        }
    }

    public class StationRegistrationRequest
    {
        public int OwnerId { get; set; }
        public string Name { get; set; }
        public decimal Latitude { get; set; }
        public decimal Longitude { get; set; }
        public string Address { get; set; }
        public decimal RatePerKwh { get; set; }
        public int TotalPlugs { get; set; }
        public string OpenTime { get; set; }
        public string CloseTime { get; set; }
        public string WhatsAppNumber { get; set; }
        public string StationPassword { get; set; }
    }

    public class StationAccessRequest
    {
        public int StationId { get; set; }
        public string Password { get; set; }
    }

    public class ApprovalRequest
    {
        public int StationId { get; set; }
        public bool Approve { get; set; }
        public int AdminId { get; set; }
    }
}
