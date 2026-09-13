using System;
using System.Net;
using System.Threading.Tasks;

namespace ServerManagerTool.Lib
{
    public class ServerStatusUpdateRegistration : IAsyncDisposable
    {
        public string InstallDirectory;
        public IPEndPoint LocalEndpoint;
        public IPEndPoint PublicEndpoint;
        public Action<IAsyncDisposable, ServerStatusUpdate> UpdateCallback;
        public Func<Task> UnregisterAction;

        public string ProfileId;

        // Used to count players via RCON "listplayers" instead of (or as a
        // fallback from) the Steam Source Query player list - the Source
        // query protocol only ever sees Steam-connected players, so a
        // cross-platform server with Epic Games players connected would
        // always undercount without this. RCON's listplayers is a
        // game-level command, so it reports every connected player
        // regardless of platform.
        public bool RCONEnabled;
        public int RCONPort;
        public string RCONPassword;

        public async ValueTask DisposeAsync()
        {
            await UnregisterAction();
        }
    }
}
