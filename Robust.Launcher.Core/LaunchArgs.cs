namespace Robust.Launcher.Core;

/// <summary>
/// Builds the engine command line and auth environment for a server, like the official launcher does.
/// </summary>
public static class LaunchArgs
{
    public static string[] BuildEngineArgs(ServerAddress address, ServerInfo info, string? username,
        IEnumerable<string>? extraCVars = null)
    {
        var args = new List<string>
        {
            "--launcher",
            "--connect-address", address.GetConnectAddress(info),
            "--ss14-address", address.Original,
        };

        if (!string.IsNullOrEmpty(username))
        {
            args.Add("--username");
            args.Add(username);
        }

        var build = info.Build;
        if (build != null)
        {
            void CVar(string name, string? value)
            {
                if (value == null) return;
                args.Add("--cvar");
                args.Add($"{name}={value}");
            }

            CVar("build.fork_id", build.ForkId);
            CVar("build.version", build.Version);
            CVar("build.engine_version", build.EngineVersion);
            CVar("build.manifest_hash", build.ManifestHash);
        }

        if (extraCVars != null)
        {
            foreach (var cvar in extraCVars)
            {
                args.Add("--cvar");
                args.Add(cvar);
            }
        }

        return args.ToArray();
    }

    /// <summary>
    /// Auth environment variables read by the engine's AuthManager.
    /// </summary>
    public static Dictionary<string, string> BuildAuthEnvironment(LoginInfo? login, ServerInfo info,
        string authServer = AuthApi.DefaultAuthServer)
    {
        var env = new Dictionary<string, string>();
        if (login == null || info.Auth?.Mode == "Disabled")
            return env;

        env["ROBUST_AUTH_USERID"] = login.UserId.ToString();
        env["ROBUST_AUTH_TOKEN"] = login.Token;
        env["ROBUST_AUTH_SERVER"] = authServer;
        if (!string.IsNullOrEmpty(info.Auth?.PublicKey))
            env["ROBUST_AUTH_PUBKEY"] = info.Auth!.PublicKey!;
        return env;
    }
}
