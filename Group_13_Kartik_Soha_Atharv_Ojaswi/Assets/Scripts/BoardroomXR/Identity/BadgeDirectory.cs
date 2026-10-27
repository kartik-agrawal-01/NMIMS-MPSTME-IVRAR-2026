using System.Collections.Generic;

namespace BoardroomXR.Identity
{
    /// <summary>
    /// The fictional corporate directory the expo demo signs people in against.
    /// Six badges on the physical-looking badge reader wall let every visitor be a
    /// separate user on one headset, which is what makes per-user gesture and
    /// behavioural templates testable at a stall: both stores are keyed by
    /// <see cref="AvatarIdentity.ClaimedUserIdString"/>.
    ///
    /// These are invented people, not real employees.
    /// </summary>
    public static class BadgeDirectory
    {
        public class Badge
        {
            public string id;
            public string name;
            public string role;

            public Badge(string id, string name, string role) { this.id = id; this.name = name; this.role = role; }
        }

        public static readonly List<Badge> Badges = new List<Badge>
        {
            new Badge("EMP-1001", "A. Mehta", "CEO"),
            new Badge("EMP-1002", "R. Iyer",  "CFO"),
            new Badge("EMP-1003", "S. Khan",  "CTO"),
            new Badge("EMP-1004", "N. Rao",   "Legal"),
            new Badge("EMP-1005", "V. Shah",  "Board"),
            new Badge("EMP-1006", "Guest",    "Auditor"),
        };

        /// <summary>Display name for an employee ID, falling back to the ID itself.</summary>
        public static string NameFor(string id)
        {
            if (string.IsNullOrEmpty(id)) return "-";
            foreach (var b in Badges) if (b.id == id) return b.name;
            return id;
        }

        public static Badge Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var b in Badges) if (b.id == id) return b;
            return null;
        }
    }
}
