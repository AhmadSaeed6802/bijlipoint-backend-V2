using System.ComponentModel.DataAnnotations.Schema;

namespace BijliPoint.Models
{
    // Add to your existing Models folder

    public class MeterReading
    {
        public int Id { get; set; }
        public int StationId { get; set; }
        public int PortNumber { get; set; }
        public DateTime Timestamp { get; set; }
        public decimal Voltage { get; set; }
        public decimal Current { get; set; }
        public decimal Power { get; set; }
        public decimal Energy { get; set; }
        public DateTime ReceivedAt { get; set; }

        public Station Station { get; set; }
    }

    public class PendingSessionData
    {
        public int RiderId    { get; set; }
        public int StationId  { get; set; }
        public int PortNumber { get; set; }
    }

    public class PortCommand
    {
        public int Id { get; set; }
        public int StationId { get; set; }
        public int PortNumber { get; set; }
        public string Command { get; set; } // "ON" or "OFF"
        public int RequestedBy { get; set; }
        public DateTime RequestedAt { get; set; }
        public DateTime? ExecutedAt { get; set; }
        public string Status { get; set; } // "Pending", "Executed", "Failed"

        public Station Station { get; set; }

        [ForeignKey("RequestedBy")] // actuall it is User.Id
        public User User { get; set; }
    }
}
