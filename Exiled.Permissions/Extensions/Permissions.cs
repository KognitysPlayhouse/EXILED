// -----------------------------------------------------------------------
// <copyright file="Permissions.cs" company="Exiled Team">
// Copyright (c) Exiled Team. All rights reserved.
// Licensed under the CC BY-SA 3.0 license.
// </copyright>
// -----------------------------------------------------------------------

namespace Exiled.Permissions.Extensions
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;

    using CommandSystem;

    using Exiled.API.Features;
    using Exiled.Permissions.Features;
    using Exiled.Permissions.Properties;

    using NorthwoodLib.Pools;

    using RemoteAdmin;

    using YamlDotNet.Serialization;
    using YamlDotNet.Serialization.NamingConventions;

    using static Exiled.Permissions.Permissions;

    /// <inheritdoc cref="Exiled.Permissions.Permissions"/>
    public static class Permissions
    {
        private static readonly ISerializer Serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreFields()
            .Build();

        private static readonly IDeserializer Deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreFields()
            .IgnoreUnmatchedProperties()
            .Build();

        /// <summary>
        /// Gets groups list.
        /// </summary>
        public static Dictionary<string, Group> Groups { get; internal set; } = new Dictionary<string, Group>();

        /// <summary>
        /// Gets the default group.
        /// </summary>
        public static Group DefaultGroup
        {
            get
            {
                foreach (var group in Groups)
                {
                    if (group.Value.IsDefault)
                        return group.Value;
                }

                return null;
            }
        }

        /// <summary>
        /// Create permissions.
        /// </summary>
        public static void Create()
        {
            if (!Directory.Exists(Instance.Config.Folder))
            {
                Log.Warn($"Permissions directory at {Instance.Config.Folder} is missing, creating.");
                Directory.CreateDirectory(Instance.Config.Folder);
            }

            if (!File.Exists(Instance.Config.FullPath))
            {
                Log.Warn($"Permissions file at {Instance.Config.FullPath} is missing, creating.");
                File.WriteAllText(Instance.Config.FullPath, Encoding.UTF8.GetString(Resources.permissions));
            }
        }

        /// <summary>
        /// Reloads permissions.
        /// </summary>
        public static void Reload()
        {
            Groups = Deserializer.Deserialize<Dictionary<string, Group>>(File.ReadAllText(Instance.Config.FullPath));

            foreach (KeyValuePair<string, Group> group in Groups.Reverse())
            {
                IEnumerable<string> inheritedPerms = new List<string>();

                inheritedPerms = Groups.Where(pair => group.Value.Inheritance.Contains(pair.Key))
                    .Aggregate(inheritedPerms, (current, pair) => current.Union(pair.Value.CombinedPermissions));

                group.Value.CombinedPermissions = group.Value.Permissions.Union(inheritedPerms).ToList();
            }
        }

        /// <summary>
        /// Save permissions.
        /// </summary>
        public static void Save() => File.WriteAllText(Instance.Config.FullPath, Serializer.Serialize(Groups));

        /// <summary>
        /// Checks a sender's permission.
        /// </summary>
        /// <param name="sender">The sender to be checked.</param>
        /// <param name="permission">The permission to be checked.</param>
        /// <returns>Returns a value indicating whether the user has the permission or not.</returns>
        public static bool CheckPermission(this ICommandSender sender, string permission) => CheckPermission(sender as CommandSender, permission);

        /// <summary>
        /// Checks a sender's permission.
        /// </summary>
        /// <param name="sender">The sender to be checked.</param>
        /// <param name="permission">The permission to be checked.</param>
        /// <returns>Returns a value indicating whether the user has the permission or not.</returns>
        public static bool CheckPermission(this CommandSender sender, string permission)
        {
            if (sender.FullPermissions || sender is ServerConsoleSender || sender is GameCore.ConsoleCommandSender)
            {
                return true;
            }
            else if (sender is PlayerCommandSender)
            {
                Player player = Player.Get(sender.SenderId);

                if (player == null)
                    return false;

                return player.CheckPermission(permission);
            }

            return false;
        }

        /// <summary>
        /// Checks a player's permission.
        /// </summary>
        /// <param name="player">The player to be checked.</param>
        /// <param name="permission">The permission to be checked.</param>
        /// <returns>true if the player's current or native group has permissions; otherwise, false.</returns>
        public static bool CheckPermission(this Player player, string permission)
        {
            if (string.IsNullOrEmpty(permission))
                return false;

            if (player == null || player.GameObject == null || Groups == null || Groups.Count == 0)
                return false;

            if (player.ReferenceHub.isDedicatedServer)
                return true;

            Log.Debug($"UserID: {player.UserId} | PlayerId: {player.Id}", Instance.Config.ShouldDebugBeShown);
            Log.Debug($"Permission string: {permission}", Instance.Config.ShouldDebugBeShown);

            var plyGroupKey = player.Group != null ? ServerStatic.GetPermissionsHandler()._groups.FirstOrDefault(g => g.Value == player.Group).Key : player.GroupName;
            if (string.IsNullOrEmpty(plyGroupKey))
                return false;

            Log.Debug($"GroupKey: {plyGroupKey}", Instance.Config.ShouldDebugBeShown);

            if (!Groups.TryGetValue(plyGroupKey, out var group))
                group = DefaultGroup;

            if (group is null)
                return false;

            const char PERM_SEPARATOR = '.';
            const string ALL_PERMS = ".*";

            if (group.CombinedPermissions.Contains(ALL_PERMS))
                return true;

            if (permission.Contains(PERM_SEPARATOR))
            {
                var strBuilder = StringBuilderPool.Shared.Rent();
                var seraratedPermissions = permission.Split(PERM_SEPARATOR);

                bool Check(string source) => group.CombinedPermissions.Contains(source, StringComparison.OrdinalIgnoreCase);

                var result = false;
                for (var z = 0; z < seraratedPermissions.Length; z++)
                {
                    if (z != 0)
                    {
                        // We need to clear the last ALL_PERMS line
                        // or it'll be like 'permission.*.subpermission'.
                        strBuilder.Length -= ALL_PERMS.Length;

                        // Separate permission groups by using its separator.
                        strBuilder.Append(PERM_SEPARATOR);
                    }

                    strBuilder.Append(seraratedPermissions[z]);

                    // If it's the last index,
                    // then we don't need to check for all permissions of the subpermission.
                    if (z == seraratedPermissions.Length - 1)
                    {
                        result = Check(strBuilder.ToString());
                        break;
                    }

                    strBuilder.Append(ALL_PERMS);
                    if (Check(strBuilder.ToString()))
                    {
                        result = true;
                        break;
                    }
                }

                StringBuilderPool.Shared.Return(strBuilder);

                return result;
            }

            // It'll work when there is no dot in the permission.
            return group.CombinedPermissions.Contains(permission, StringComparison.OrdinalIgnoreCase);
        }
    }
}
