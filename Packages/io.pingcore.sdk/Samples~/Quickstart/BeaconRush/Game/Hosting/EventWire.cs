using System;

namespace BeaconRush.Hosting
{
    /// <summary>Event literals of SDK enums: the member name with a lowercase first letter (<c>EndpointClosed</c> is <c>endpointClosed</c>).</summary>
    public static class EventWire
    {
        public static string Camel(Enum value)
        {
            if (value == null)
            {
                return null;
            }

            string name = value.ToString();
            return name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);
        }
    }
}
