using System.Text.Json.Serialization;

namespace CtYun.Models
{
    public class MultiAccountConfig
    {
        [JsonPropertyName("accounts")]
        public List<AccountCredential> Accounts { get; set; } = [];
    }

    public class AccountCredential
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("user")]
        public string User { get; set; }

        [JsonPropertyName("password")]
        public string Password { get; set; }

        [JsonPropertyName("deviceCode")]
        public string DeviceCode { get; set; }
    }
}
