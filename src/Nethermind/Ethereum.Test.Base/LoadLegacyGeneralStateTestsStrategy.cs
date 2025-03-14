// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Ethereum.Test.Base.Interfaces;

namespace Ethereum.Test.Base
{
    public class LoadLegacyGeneralStateTestsStrategy : ITestLoadStrategy
    {
        public IEnumerable<EthereumTest> Load(string testsDirectoryName, string wildcard = null)
        {
            IEnumerable<string> testDirs;
            if (!Path.IsPathRooted(testsDirectoryName))
            {
                string legacyTestsDirectory = GetLegacyGeneralStateTestsDirectory();

                testDirs = Directory.EnumerateDirectories(legacyTestsDirectory, testsDirectoryName, new EnumerationOptions { RecurseSubdirectories = true });
            }
            else
            {
                testDirs = new[] { testsDirectoryName };
            }

            List<GeneralStateTest> testJsons = new();
            foreach (string testDir in testDirs)
            {
                testJsons.AddRange(LoadTestsFromDirectory(testDir, wildcard));
            }

            return testJsons;
        }

        private string GetLegacyGeneralStateTestsDirectory()
        {
            string currentDirectory = AppDomain.CurrentDomain.BaseDirectory;

            return Path.Combine(currentDirectory.Remove(currentDirectory.LastIndexOf("src")), "src", "tests", "LegacyTests", "Constantinople", "GeneralStateTests");
        }

        private IEnumerable<GeneralStateTest> LoadTestsFromDirectory(string testDir, string wildcard)
        {
            List<GeneralStateTest> testsByName = new();
            IEnumerable<string> testFiles = Directory.EnumerateFiles(testDir);

            foreach (string testFile in testFiles)
            {
                FileTestsSource fileTestsSource = new(testFile, wildcard);
                try
                {
                    var tests = fileTestsSource.LoadGeneralStateTests();
                    foreach (GeneralStateTest blockchainTest in tests)
                    {
                        blockchainTest.Category = testDir;
                    }

                    testsByName.AddRange(tests);
                }
                catch (Exception e)
                {
                    testsByName.Add(new GeneralStateTest { Name = testFile, LoadFailure = $"Failed to load: {e}" });
                }
            }

            return testsByName;
        }
    }
}
