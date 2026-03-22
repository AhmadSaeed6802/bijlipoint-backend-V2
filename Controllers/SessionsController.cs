using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BijliPoint.Data;

namespace BijliPoint.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SessionsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public SessionsController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet("user/{userId}")]
        public async Task<IActionResult> GetUserSessions(int userId)
        {
            var sessions = await _context.ChargingSessions
                .Where(s => s.RiderId == userId)
                .OrderByDescending(s => s.StartTime)
                .ToListAsync();

            return Ok(sessions);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetSession(int id)
        {
            var session = await _context.ChargingSessions.FindAsync(id);
            if (session == null)
                return NotFound();

            return Ok(session);
        }
    }
}
