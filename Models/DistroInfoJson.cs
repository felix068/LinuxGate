using System.Text.Json.Serialization;

namespace LinuxGate.Models
{
    public class DistroInfoJson
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("imageUrl")]
        public string ImageUrl { get; set; }

        [JsonPropertyName("isoUrl")]
        public string IsoUrl { get; set; }

        [JsonPropertyName("isoInstaller")]
        public string IsoInstaller { get; set; }

        [JsonPropertyName("isoInstallerFileName")]
        public string IsoInstallerFileName { get; set; }
    }
}
