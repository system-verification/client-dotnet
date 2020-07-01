﻿namespace ReportPortal.Shared.Execution
{
    /// <summary>
    /// Returns context to amend current test metadata or add log messages.
    /// </summary>
    public interface ITestContext : ILogContext
    {
        /// <summary>
        /// Commands emitter to modify metadata of test on fly.
        /// </summary>
        ITestMetadataEmitter Metadata { get; }
    }
}
