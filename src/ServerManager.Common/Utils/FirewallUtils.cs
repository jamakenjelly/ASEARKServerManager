using System;
using System.Collections.Generic;
using System.Linq;
using WindowsFirewallHelper;
using WindowsFirewallHelper.FirewallRules;

/*
WindowsFirewallHelper
A class library to manage the Windows Firewall as well as adding your program to the Windows Firewall Exception list

=========================================================================
USAGE
    Get an instance of the active firewall  using FirewallManager class and
    use the properties to get the list of firewall rules and profiles. 
    You can also  use the methods  on this class  to add a new rule  to the 
    firewall.

CODE SAMPLE FOR ADDING AN APPLICATION RULE TO THE ACTIVE PROFILE:
    var rule = FirewallManager.Instance.CreateApplicationRule(FirewallManager.Instance.GetProfile().Type, @"MyApp Rule", FirewallAction.Allow, @"C:\MyApp.exe"); 
    rule.Direction = FirewallDirection.Outbound;
    FirewallManager.Instance.Rules.Add(rule);

CODE SAMPLE FOR ADDING A PORT RULE TO THE ACTIVE PROFILE:
    var rule = FirewallManager.Instance.CreatePortRule(FirewallManager.Instance.GetProfile().Type, @"Port 80 - Any Protocol", FirewallAction.Allow, 80, FirewallProtocol.Any);
    FirewallManager.Instance.Rules.Add(rule);

CODE SAMPLE TO GET A LIST OF ALL REGISTERED RULES:
    var allRules = FirewallManager.Instance.Rules.ToArray();

MORE SAMPLES:
    Check the Project's Github page at: https://github.com/falahati/WindowsFirewallHelper
 */


namespace ServerManagerTool.Common.Utils
{
    /// <summary>
    /// Code for dealing with firewalls
    /// </summary>
    public static class FirewallUtils
    {
        // The Windows Firewall COM API rejects a handful of characters outright
        // in rule names, groupings and descriptions - "|" (its own field
        // delimiter in the underlying registry format) being the one that
        // actually bites here, since ARK server names very commonly contain
        // them (e.g. "MY SERVER | 5X | PVP"). Passing one straight through
        // makes INetFwRules::Add fail with E_INVALIDARG, which marshals to an
        // ArgumentException reading "Value does not fall within the expected
        // range." - a message that gives no hint whatsoever that the server
        // name was the problem. Strip them here rather than letting a server
        // fail to start over its name.
        private static string SanitizeFirewallText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;

            var sanitized = new string(value.Where(c => c != '|' && !char.IsControl(c)).ToArray()).Trim();

            // "all" is reserved by the firewall API for rule groupings.
            if (sanitized.Equals("all", StringComparison.OrdinalIgnoreCase))
                sanitized = $"{sanitized}_";

            return sanitized;
        }

        public static bool CreateFirewallRules(string exeName, List<int> ports, string ruleName, string description = "", string group = "")
        {
            if (!FirewallWAS.IsSupported(new COMTypeResolver()))
                return false;

            // Callers treat a false return as "couldn't set the rules" and
            // handle it gracefully (prompting the user to carry on without
            // them). Letting a COM failure escape instead would propagate all
            // the way out of ServerRuntime.StartAsync and abort the server
            // start outright, showing the user nothing but the raw exception
            // message - firewall management is a convenience, never a reason
            // to refuse to launch the server.
            try
            {
                ruleName = SanitizeFirewallText(ruleName);
                description = SanitizeFirewallText(description);
                group = SanitizeFirewallText(group);

                var firewallManager = FirewallManager.Instance;

                DeleteFirewallRules(exeName);

                // create the TCP rule
                var rule = firewallManager.CreateApplicationRule(FirewallProfiles.Domain | FirewallProfiles.Private | FirewallProfiles.Public, $"{ruleName} TCP", FirewallAction.Allow, exeName, FirewallProtocol.TCP);
                if (rule != null)
                {
                    rule.Direction = FirewallDirection.Inbound;
                    rule.IsEnable = true;
                    rule.LocalPorts = ports.Select(p => (ushort)p).ToArray();

                    if (rule is FirewallWASRule wasRule)
                    {
                        wasRule.Description = description;
                        wasRule.Grouping = group;
                    }
                    firewallManager.Rules.Add(rule);
                }

                // create the UDP rule
                rule = firewallManager.CreateApplicationRule(FirewallProfiles.Domain | FirewallProfiles.Private | FirewallProfiles.Public, $"{ruleName} UDP", FirewallAction.Allow, exeName, FirewallProtocol.UDP);
                if (rule != null)
                {
                    rule.Direction = FirewallDirection.Inbound;
                    rule.IsEnable = true;
                    rule.LocalPorts = ports.Select(p => (ushort)p).ToArray();

                    if (rule is FirewallWASRule wasRule)
                    {
                        wasRule.Description = description;
                        wasRule.Grouping = group;
                    }
                    firewallManager.Rules.Add(rule);
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool DeleteFirewallRules(string exeName)
        {
            if (!FirewallWAS.IsSupported(new COMTypeResolver()))
                return false;

            try
            {
                var firewallManager = FirewallManager.Instance;

                // check for existing rules
                var rulesToDelete = firewallManager.Rules.Cast<IFirewallRule>().Where(r => !string.IsNullOrWhiteSpace(r.ApplicationName) && r.ApplicationName.Equals(exeName, StringComparison.OrdinalIgnoreCase)).ToList();

                if (rulesToDelete != null && rulesToDelete.Count > 0)
                {
                    // delete the existing rules
                    rulesToDelete.ForEach(r => firewallManager.Rules.Remove(r));
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
