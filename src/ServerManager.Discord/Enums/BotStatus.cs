namespace ServerManagerTool.DiscordBot.Enums
{
    public enum BotState
    {
        Unknown,
        Disabled,
        Stopped,
        // The background run-loop is alive (Started) but the gateway
        // connection isn't actually up yet - either mid-handshake right
        // after starting, or reconnecting after a drop. Distinct from
        // Running so the Task Status panel stops claiming "Running" while
        // every Discord post is actually failing with "not connected".
        Connecting,
        Running,
    }
}
