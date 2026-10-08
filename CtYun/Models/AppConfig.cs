using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CtYun.Models
{
    public class AppConfig
    {
        [JsonPropertyName("accounts")]
        public List<AccountConfig> Accounts { get; set; } = [];

        [JsonPropertyName("keepAliveSeconds")]
        public int KeepAliveSeconds { get; set; } = 60;

        /// <summary>会话轮换周期（分钟）。0 表示关闭轮换，行为与旧版本一致。</summary>
        [JsonPropertyName("restartIntervalMinutes")]
        public int RestartIntervalMinutes { get; set; } = 30;

        /// <summary>每次会话时长附加的随机抖动（秒），用于错峰，避免多账号同时重新登录。</summary>
        [JsonPropertyName("restartJitterSeconds")]
        public int RestartJitterSeconds { get; set; } = 60;

        /// <summary>会话之间的冷却时间（秒），给服务端释放旧会话的时间。</summary>
        [JsonPropertyName("sessionCooldownSeconds")]
        public int SessionCooldownSeconds { get; set; } = 10;
    }

    public class AccountConfig
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
