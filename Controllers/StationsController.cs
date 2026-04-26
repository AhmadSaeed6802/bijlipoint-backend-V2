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

        [HttpPost("register")]
        public async Task<IActionResult> RegisterStation([FromBody] StationRegistrationRequest request)
        {
            if (string.IsNullOrEmpty(request.StationID))
                return BadRequest(new { error = "StationID is required (e.g., LUMS123456789101)" });

            if (request.StationID.Length != 16)
                return BadRequest(new { error = "StationID must be exactly 16 characters: 4-letter name + 12-digit MAC (e.g., LUMS123456789101)" });

            if (await _context.Stations.AnyAsync(s => s.StationID == request.StationID.ToUpper()))
                return BadRequest(new { error = "This StationID already exists. Please use a unique ID." });

            // Validate password
            if (string.IsNullOrEmpty(request.StationPassword) || request.StationPassword.Length < 6)
            {
                return BadRequest(new { error = "Station password must be at least 6 characters" });
            }

            var station = new Station
            {
                OwnerId = request.OwnerId,
                Name = request.Name,
                Latitude = request.Latitude,
                Longitude = request.Longitude,
                Address = request.Address,
                RatePerKwh = request.RatePerKwh,
                
                // ✅ NEW: Store StationID
                StationID = request.StationID.ToUpper(),
                
                TotalPlugs = request.TotalPlugs,
                AvailablePlugs = request.TotalPlugs,
                IsOpen = true,
                OpenTime = string.IsNullOrEmpty(request.OpenTime)
                    ? TimeSpan.Parse("00:00")
                    : TimeSpan.Parse(request.OpenTime),
                CloseTime = string.IsNullOrEmpty(request.CloseTime)
                    ? TimeSpan.Parse("23:59")
                    : TimeSpan.Parse(request.CloseTime),
                WhatsAppNumber = request.WhatsAppNumber,
                IsActive = false,
                StationPassword = BCrypt.Net.BCrypt.HashPassword(request.StationPassword),
                ApprovalStatus = "Pending",
                CreatedAt = DateTime.UtcNow
            };

            _context.Stations.Add(station);
            await _context.SaveChangesAsync();

            return Ok(new
            {
                id = station.Id,
                stationID = station.StationID,
                message = $"Station registered with ID: {station.StationID}. Waiting for admin approval."
            });
        }

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
                    s.StationID,  // ✅ NEW: Include StationID
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
                id = station.Id,
                stationID = station.StationID,
                name = station.Name,
                totalPlugs = station.TotalPlugs,
                message = "Access granted"
            });
        }

        [HttpGet("pending")]
        public async Task<IActionResult> GetPendingStations()
        {
            var pending = await _context.Stations
                .Where(s => s.ApprovalStatus == "Pending")
                .Select(s => new
                {
                    s.Id,
                    s.Name,
                    s.Address,
                    s.StationID,  // ✅ NEW: Include StationID
                    s.TotalPlugs,
                    s.RatePerKwh,
                    s.OwnerId,
                    s.CreatedAt
                })
                .OrderBy(s => s.CreatedAt)
                .ToListAsync();

            return Ok(pending);
        }

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
                id = station.Id,
                stationID = station.StationID
            });
        }

        [HttpGet("stats")]
        public async Task<IActionResult> GetStats()
        {
            var totalApproved = await _context.Stations.CountAsync(s => s.ApprovalStatus == "Approved");
            var totalPending = await _context.Stations.CountAsync(s => s.ApprovalStatus == "Pending");
            var totalRiders = await _context.Users.CountAsync(u => u.Role == "Rider" && u.IsActive);
            var totalOwners = await _context.Users.CountAsync(u => u.Role == "StationOwner" && u.IsActive);

            return Ok(new { totalApproved, totalPending, totalRiders, totalOwners });
        }

        // GET: api/stations/approved  — public list for rider station browser
        [HttpGet("approved")]
        public async Task<IActionResult> GetApprovedStations()
        {
            var stations = await _context.Stations
                .Where(s => s.ApprovalStatus == "Approved" && s.IsActive)
                .Select(s => new
                {
                    s.Id,
                    s.Name,
                    s.Address,
                    s.Latitude,
                    s.Longitude,
                    s.StationID,
                    s.TotalPlugs,
                    s.RatePerKwh,
                    s.OpenTime,
                    s.CloseTime,
                    s.WhatsAppNumber
                })
                .OrderBy(s => s.Name)
                .ToListAsync();

            return Ok(stations);
        }

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
                station.StationID,  // ✅ NEW: Include StationID
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
        public string StationID { get; set; }  // ✅ NEW: StationID (12 chars)
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
