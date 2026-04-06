namespace BijliPoint.Models
{
    public class User
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
        public string Phone { get; set; }

        public string CNIC { get; set; }
        public string PasswordHash { get; set; }
        public string Role { get; set; } // SuperAdmin, Admin, StationOwner, Rider
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
    }

        public class Station
        {
            public int Id { get; set; }
            public int OwnerId { get; set; }
            public string Name { get; set; }
            public decimal Latitude { get; set; }
            public decimal Longitude { get; set; }
            public string Address { get; set; }
            public decimal RatePerKwh { get; set; }
            public int TotalPlugs { get; set; }
            public int AvailablePlugs { get; set; }
            public bool IsOpen { get; set; }
            public TimeSpan OpenTime { get; set; }
            public TimeSpan CloseTime { get; set; }
            public string WhatsAppNumber { get; set; }
            public bool IsActive { get; set; }
            public DateTime CreatedAt { get; set; }

            // NEW: Station management
            public string StationPassword { get; set; }
            public string ApprovalStatus { get; set; } // Pending, Approved, Rejected
            public int? ApprovedBy { get; set; }
            public DateTime? ApprovedAt { get; set; }
        }

    public class ChargingSession
    {
        public int Id { get; set; }
        public int RiderId { get; set; }
        public int StationId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public decimal UnitsConsumed { get; set; }
        public decimal TotalCost { get; set; }
        public string Status { get; set; } // Active, Completed, Cancelled
        public string BreakerSessionId { get; set; }
    }
}
