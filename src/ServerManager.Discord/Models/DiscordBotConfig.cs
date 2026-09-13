using ServerManagerTool.DiscordBot.Enums;

namespace ServerManagerTool.DiscordBot.Models
{
    public class DiscordBotConfig
    {
        public LogLevel LogLevel { get; set; } = LogLevel.Info;

        public int MaxArchiveDays { get; set; } = 30;

        public int MaxArchiveFiles { get; set; } = 30;

        public string DiscordToken { get; set; } = string.Empty;

        // If set, the bot only responds to prefix commands sent in this channel.
        // Other channels are ignored, so control commands can be kept separate
        // from general chat.
        public string AdminChannelId { get; set; } = string.Empty;

        // Comma-separated Discord user IDs allowed to use the admin control panel
        // buttons (Start/Stop/Restart/Update/Backup). Empty means anyone who can
        // see the channel can click them.
        public string AllowedAdminIds { get; set; } = string.Empty;

        public string CommandPrefix { get; set; } = string.Empty;

        public string DataDirectory { get; set; } = string.Empty;

        public bool AllowAllBots { get; set; } = false;

        public DiscordBotWhitelist DiscordBotWhitelist { get; set; } = new DiscordBotWhitelist();
    }
}
