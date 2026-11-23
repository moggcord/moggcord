/*
 * Copyright (c) 2026 Moggcord contributors
 * SPDX-License-Identifier: GPL-3.0-or-later
 */

using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace MoggcordInstaller
{
    internal static class InstallerLog
    {
        private const long MaxBytes = 512 * 1024;
        private static readonly object Gate = new object();

        internal static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Moggcord", "installer.log");

        internal static readonly bool IsElevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        internal static void Write(string message)
        {
            Console.WriteLine("[Moggcord] " + message);
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                        File.Move(FilePath, FilePath + ".old", true);
                    File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}", Encoding.UTF8);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine("The installer log could not be written: " + error.Message);
                }
            }
        }
    }
}
