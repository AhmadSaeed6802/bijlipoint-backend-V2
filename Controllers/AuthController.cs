using BCrypt.Net;
using BijliPoint.Data;
using BijliPoint.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Mail;
using System.Security.Claims;
using System.Text;

namespace BijliPoint.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _config;

        public AuthController(ApplicationDbContext context, IConfiguration config)
        {
            _context = context;
            _config = config;
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequest request)
        {
            if (string.IsNullOrEmpty(request.CNIC) || request.CNIC.Length < 13)
                return BadRequest(new { error = "CNIC is required (13-15 digits)" });

            if (_context.Users.Any(u => u.Email == request.Email))
                return BadRequest(new { error = "Email already registered" });

            if (_context.Users.Any(u => u.Phone == request.Phone))
                return BadRequest(new { error = "Phone number already registered" });

            if (_context.Users.Any(u => u.CNIC == request.CNIC))
                return BadRequest(new { error = "CNIC already registered" });

            var user = new User
            {
                Name = request.Name,
                Email = request.Email,
                Phone = request.Phone,
                CNIC = request.CNIC,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password, 11),
                Role = request.Role,
                CreatedAt = DateTime.UtcNow,
                IsActive = true
            };

            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            var token = GenerateJwtToken(user);

            return Ok(new
            {
                user = new
                {
                    id = user.Id,
                    name = user.Name,
                    email = user.Email,
                    phone = user.Phone,
                    cnic = user.CNIC,
                    role = user.Role
                },
                token
            });
        }

        [HttpPost("login")]
        public IActionResult Login([FromBody] LoginRequest request)
        {
            var user = _context.Users.FirstOrDefault(u => u.Email == request.Email);

            if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
                return Unauthorized(new { error = "Invalid credentials" });

            if (!user.IsActive)
                return Unauthorized(new { error = "Account is disabled" });

            var token = GenerateJwtToken(user);

            return Ok(new
            {
                user = new
                {
                    id = user.Id,
                    name = user.Name,
                    email = user.Email,
                    role = user.Role
                },
                token
            });
        }


        #region password reset
        // Add to your existing AuthController class

        [HttpPost("forgot-password")]
        public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
        {
            try
            {
                var user = await _context.Users
                    .FirstOrDefaultAsync(u => u.Email == request.Email);

                if (user == null)
                    return Ok(new { message = "If email exists, reset link sent" });

                var resetToken = Guid.NewGuid().ToString();
                var expiry = DateTime.UtcNow.AddMinutes(15);

                var tokenRecord = new PasswordResetToken
                {
                    UserId = user.Id,
                    Token = resetToken,
                    ExpiresAt = expiry,
                    CreatedAt = DateTime.UtcNow,
                    IsUsed = false
                };

                _context.PasswordResetTokens.Add(tokenRecord);
                await _context.SaveChangesAsync();

                // Dynamic URL based on environment
                var frontendUrl = _config["AppSettings:FrontendUrl"] ?? "https://bijlipoint.com";
                var resetLink = $"{frontendUrl}/reset-password?token={resetToken}";

                await SendResetEmail(user.Email, resetLink);

                return Ok(new { message = "Password reset link sent" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Forgot password error: {ex.Message}");
                return StatusCode(500, new { error = "Failed to process request" });
            }
        }

        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
        {
            var tokenRecord = await _context.PasswordResetTokens
                .FirstOrDefaultAsync(t =>
                    t.Token == request.Token &&
                    t.ExpiresAt > DateTime.UtcNow &&
                    !t.IsUsed
                );

            if (tokenRecord == null)
                return BadRequest(new { error = "Invalid or expired token" });

            var user = await _context.Users.FindAsync(tokenRecord.UserId);
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword, 11);
            tokenRecord.IsUsed = true;

            await _context.SaveChangesAsync();

            return Ok(new { message = "Password reset successful" });
        }

        private async Task SendResetEmail(string email, string resetLink)
        {
            try
            {
                var smtpServer = _config["Email:SmtpServer"];
                var smtpPortStr = _config["Email:SmtpPort"];
                var username = _config["Email:Username"];
                var password = _config["Email:Password"];

                // Validate config
                if (string.IsNullOrEmpty(smtpPortStr))
                    throw new Exception("SmtpPort not configured");

                int smtpPort = int.Parse(smtpPortStr);

                using var client = new SmtpClient(smtpServer, smtpPort)
                {
                    EnableSsl = true,
                    Credentials = new NetworkCredential(username, password)
                };

                var mailMessage = new MailMessage
                {
                    From = new MailAddress(username, "BijliPoint"),
                    Subject = "Reset Your Password - BijliPoint",
                    Body = $@"
                <!DOCTYPE html>
                <html>
                <body style='font-family: Arial; padding: 20px;'>
                    <h2 style='color: #10B981;'>Reset Your Password</h2>
                    <p>You requested to reset your password.</p>
                    <p>Click the button below to reset:</p>
                    <a href='{resetLink}' 
                       style='display: inline-block; padding: 12px 24px; 
                              background: #10B981; color: white; 
                              text-decoration: none; border-radius: 5px; margin: 20px 0;'>
                        Reset Password
                    </a>
                    <p><small>Or copy this link: {resetLink}</small></p>
                    <p style='color: #666; font-size: 14px;'>
                        This link expires in 15 Minutes.<br>
                        If you didn't request this, please ignore this email.
                    </p>
                </body>
                </html>
            ",
                    IsBodyHtml = true
                };
                mailMessage.To.Add(email);

                await client.SendMailAsync(mailMessage);
            }
            catch (Exception ex)
            {
                // Log error but don't expose to user
                Console.WriteLine($"Email send failed: {ex.Message}");
                throw;
            }
        }

        // Add at bottom of file
        public class ForgotPasswordRequest { public string Email { get; set; } }
        public class ResetPasswordRequest { public string Token { get; set; } public string NewPassword { get; set; } }
        #endregion


        private string GenerateJwtToken(User user)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.Name, user.Name),
                new Claim(ClaimTypes.Role, user.Role)
            };

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config["Jwt:Secret"]));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _config["Jwt:Issuer"],
                audience: _config["Jwt:Audience"],
                claims: claims,
                expires: DateTime.Now.AddDays(7),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }

    public class RegisterRequest
    {
        public string Name { get; set; }
        public string Email { get; set; }
        public string Phone { get; set; }
        public string CNIC { get; set; }
        public string Password { get; set; }
        public string Role { get; set; }
    }

    public class LoginRequest
    {
        public string Email { get; set; }
        public string Password { get; set; }
    }
}
