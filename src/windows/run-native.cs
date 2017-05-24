/********************************************************************++
Copyright (c) Microsoft Corporation.  All rights reserved.
--********************************************************************/
/*
 * Copied from NativeCommandProcessor.cs in the Powershell source code.
 */

using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Text;
using System.Collections;
using System.Threading;
using System.Management.Automation.Internal;
using System.Management.Automation.Runspaces;
using System.Xml;
using System.Runtime.InteropServices;
using Dbg = System.Management.Automation.Diagnostics;
using System.Runtime.Serialization;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace com.cspotcode
{
    /// Various types of input/output supported by native commands.
    /// <remarks>
    /// Most native commands only support text. Other formats
    /// are supported by minishell
    /// </remarks>
    internal enum NativeCommandIOFormat
    {
        Text,
        Xml
    };

    /// Different streams produced by minishell output
    internal enum MinishellStream
    {
        Output,
        Error,
        Verbose,
        Warning,
        Debug,
        Progress,
        Information,
        Unknown
    }

    /// Helper class which holds stream names and also provide conversion
    /// method
    internal static class StringToMinishellStreamConverter
    {
        internal const string OutputStream = "output";
        internal const string ErrorStream = "error";
        internal const string DebugStream = "debug";
        internal const string VerboseStream = "verbose";
        internal const string WarningStream = "warning";
        internal const string ProgressStream = "progress";
        internal const string InformationStream = "information";

        internal static MinishellStream ToMinishellStream(string stream)
        {
            Dbg.Assert(stream != null, "caller should validate the parameter");

            MinishellStream ms = MinishellStream.Unknown;
            if (OutputStream.Equals(stream, StringComparison.OrdinalIgnoreCase))
            {
                ms = MinishellStream.Output;
            }
            else if (ErrorStream.Equals(stream, StringComparison.OrdinalIgnoreCase))
            {
                ms = MinishellStream.Error;
            }
            else if (DebugStream.Equals(stream, StringComparison.OrdinalIgnoreCase))
            {
                ms = MinishellStream.Debug;
            }
            else if (VerboseStream.Equals(stream, StringComparison.OrdinalIgnoreCase))
            {
                ms = MinishellStream.Verbose;
            }
            else if (WarningStream.Equals(stream, StringComparison.OrdinalIgnoreCase))
            {
                ms = MinishellStream.Warning;
            }
            else if (ProgressStream.Equals(stream, StringComparison.OrdinalIgnoreCase))
            {
                ms = MinishellStream.Progress;
            }
            else if (InformationStream.Equals(stream, StringComparison.OrdinalIgnoreCase))
            {
                ms = MinishellStream.Information;
            }

            return ms;
        }
    }


    internal class ProcessOutputObject
    {
        internal object Data { get; }

        internal MinishellStream Stream { get; }

        internal ProcessOutputObject(object data, MinishellStream stream)
        {
            Data = data;
            Stream = stream;
        }
    }

    /// Provides way to create and execute native commands.
    internal class NativeCommandProcessor : CommandProcessorBase
    {
        #region ctor/native command properties

        /// Information about application which is invoked by this instance of
        /// NativeCommandProcessor
        private ApplicationInfo _applicationInfo;

        /// Initializes the new instance of NativeCommandProcessor class.
        ///
        /// <param name="applicationInfo">
        /// The information about the application to run.
        /// </param>
        ///
        /// <param name="context">
        /// The execution context for this command.
        /// </param>
        ///
        /// <exception cref="ArgumentNullException">
        /// <paramref name="applicationInfo"/> or <paramref name="context"/> is null
        /// </exception>
        internal NativeCommandProcessor(ApplicationInfo applicationInfo, ExecutionContext context)
            : base(applicationInfo)
        {
            if (applicationInfo == null)
            {
                throw PSTraceSource.NewArgumentNullException("applicationInfo");
            }

            _applicationInfo = applicationInfo;
            this._context = context;

            this.Command = new NativeCommand();
            this.Command.CommandInfo = applicationInfo;
            this.Command.Context = context;
            this.Command.commandRuntime = this.commandRuntime = new MshCommandRuntime(context, applicationInfo, this.Command);

            this.CommandScope = context.EngineSessionState.CurrentScope;

            //provide native command a backpointer to this object.
            //When kill is called on the command object,
            //it calls this NCP back to kill the process...
            ((NativeCommand)Command).MyCommandProcessor = this;

            //Create input writer for providing input to the process.
            _inputWriter = new ProcessInputWriter(Command);
        }

        /// Gets the NativeCommand associated with this command processor.
        private NativeCommand nativeCommand
        {
            get
            {
                NativeCommand command = this.Command as NativeCommand;
                Diagnostics.Assert(command != null, "this.Command is created in the constructor.");
                return command;
            }
        }

        /// Gets or sets the name of the native command.
        private string NativeCommandName
        {
            get
            {
                string name = _applicationInfo.Name;
                return name;
            }
        }

        /// Gets or sets path to the native command.
        private string Path
        {
            get
            {
                string path = _applicationInfo.Path;
                return path;
            }
        }

        #endregion ctor/native command properties

        #region parameter binder

        /// Variable which is set to true when prepare is called.
        /// Parameter Binder should only be created after Prepare method is called
        private bool _isPreparedCalled = false;

        /// Parameter binder used by this command processor
        private NativeCommandParameterBinderController _nativeParameterBinderController;

        /// Gets a new instance of a ParameterBinderController using a NativeCommandParameterBinder
        ///
        /// <param name="command">
        /// The native command to be run.
        /// </param>
        ///
        /// <returns>
        /// A new parameter binder controller for the specified command.
        /// </returns>
        ///
        internal ParameterBinderController NewParameterBinderController(InternalCommand command)
        {
            Dbg.Assert(_isPreparedCalled, "parameter binder should not be created before prepared is called");

            _nativeParameterBinderController =
                new NativeCommandParameterBinderController(
                    this.nativeCommand);

            return _nativeParameterBinderController;
        }

        internal NativeCommandParameterBinderController NativeParameterBinderController
        {
            get
            {
                if (_nativeParameterBinderController == null)
                {
                    NewParameterBinderController(this.Command);
                }
                return _nativeParameterBinderController;
            }
        }

        #endregion parameter binder

        #region internal overrides

        /// Prepares the command for execution with the specified CommandParameterInternal.
        internal override void Prepare(IDictionary psDefaultParameterValues)
        {
            _isPreparedCalled = true;

            this.NativeParameterBinderController.BindParameters(arguments);

            InitNativeProcess();
        }

        /// Executes the command. This method assumes that Prepare is already called.
        internal override void ProcessRecord()
        {
            while (Read())
            {
                _inputWriter.Add(Command.CurrentPipelineObject);
            }

            ConsumeAvailableNativeProcessOutput(blocking: false);
        }

        /// Process object for the invoked application
        private System.Diagnostics.Process _nativeProcess;

        /// This is used for writing input to the process
        private ProcessInputWriter _inputWriter = null;

        /// Is true if this command is to be run "standalone" - that is, with
        /// no redirection.
        private bool _runStandAlone;

        /// Indicate whether we need to consider redirecting the output/error of the current native command.
        /// Usually a windows program which is the last command in a pipeline can be executed as 'background' -- we don't need to capture its output/error streams.
        private bool _isRunningInBackground;

        /// This output queue helps us keep the output and error (if redirected) order correct.
        /// We could do a blocking read in the Complete block instead,
        /// but then we would not be able to restore the order reasonable.
        private BlockingCollection<ProcessOutputObject> _nativeProcessOutputQueue;

        private bool _scrapeHostOutput;

        private Host.Coordinates _startPosition;

        /// object used for synchronization between StopProcessing thread and
        /// Pipeline thread.
        private object _sync = new object();

        /// Executes the native command once all of the input has been gathered.
        /// <exception cref="PipelineStoppedException">
        /// The pipeline is stopping
        /// </exception>
        /// <exception cref="ApplicationFailedException">
        /// The native command could not be run
        /// </exception>
        private void InitNativeProcess()
        {
            // Figure out if we're going to run this process "standalone" i.e. without
            // redirecting anything. This is a bit tricky as we always run redirected so
            // we have to see if the redirection is actually being done at the topmost level or not.

            //Calculate if input and output are redirected.
            bool redirectOutput;
            bool redirectError;
            bool redirectInput;

            CalculateIORedirection(out redirectOutput, out redirectError, out redirectInput);

            // Find out if it's the only command in the pipeline.
            bool soloCommand = this.Command.MyInvocation.PipelineLength == 1;

            // Get the start info for the process.
            ProcessStartInfo startInfo = GetProcessStartInfo(redirectOutput, redirectError, redirectInput, soloCommand);

            if (this.Command.Context.CurrentPipelineStopping)
            {
                throw new PipelineStoppedException();
            }

            _startPosition = new Host.Coordinates();
            _scrapeHostOutput = false;

            Exception exceptionToRethrow = null;
            try
            {
                // If this process is being run standalone, tell the host, which might want
                // to save off the window title or other such state as might be tweaked by
                // the native process
                if (!redirectOutput)
                {
                    this.Command.Context.EngineHostInterface.NotifyBeginApplication();

                    // Also, store the Raw UI coordinates so that we can scrape the screen after
                    // if we are transcribing.
                    try
                    {
                        if (this.Command.Context.EngineHostInterface.UI.IsTranscribing)
                        {
                            _scrapeHostOutput = true;
                            _startPosition = this.Command.Context.EngineHostInterface.UI.RawUI.CursorPosition;
                            _startPosition.X = 0;
                        }
                    }
                    catch (Host.HostException)
                    {
                        // The host doesn't support scraping via its RawUI interface
                        _scrapeHostOutput = false;
                    }
                }

                //Start the process. If stop has been called, throw exception.
                //Note: if StopProcessing is called which this method has the lock,
                //Stop thread will wait for nativeProcess to start.
                //If StopProcessing gets the lock first, then it will set the stopped
                //flag and this method will throw PipelineStoppedException when it gets
                //the lock.
                lock (_sync)
                {
                    if (_stopped)
                    {
                        throw new PipelineStoppedException();
                    }

                    try
                    {
                        _nativeProcess = new Process();
                        _nativeProcess.StartInfo = startInfo;
                        _nativeProcess.Start();
                    }
                    catch (Win32Exception)
                    {
#if CORECLR             // Shell doesn't exist on OneCore, so a file cannot be associated with an executable,
                        // and we cannot run an executable as 'ShellExecute' either.
                        throw;
#else
                        // See if there is a file association for this command. If so
                        // then we'll use that. If there's no file association, then
                        // try shell execute...
                        string executable = FindExecutable(startInfo.FileName);
                        bool notDone = true;
                        if (!String.IsNullOrEmpty(executable))
                        {
                            if (IsConsoleApplication(executable))
                            {
                                // Allocate a console if there isn't one attached already...
                                ConsoleVisibility.AllocateHiddenConsole();
                            }

                            string oldArguments = startInfo.Arguments;
                            string oldFileName = startInfo.FileName;
                            startInfo.Arguments = "\"" + startInfo.FileName + "\" " + startInfo.Arguments;
                            startInfo.FileName = executable;
                            try
                            {
                                _nativeProcess.Start();
                                notDone = false;
                            }
                            catch (Win32Exception)
                            {
                                // Restore the old filename and arguments to try shell execute last...
                                startInfo.Arguments = oldArguments;
                                startInfo.FileName = oldFileName;
                            }
                        }
                        // We got here because there was either no executable found for this
                        // file or we tried to launch the exe and it failed. In either case
                        // we will try launching one last time using ShellExecute...
                        if (notDone)
                        {
                            if (soloCommand && startInfo.UseShellExecute == false)
                            {
                                startInfo.UseShellExecute = true;
                                startInfo.RedirectStandardInput = false;
                                startInfo.RedirectStandardOutput = false;
                                startInfo.RedirectStandardError = false;
                                _nativeProcess.Start();
                            }
                            else
                            {
                                throw;
                            }
                        }
#endif
                    }
                }

                if (this.Command.MyInvocation.PipelinePosition < this.Command.MyInvocation.PipelineLength)
                {
                    // Never background unless you're at the end of a pipe.
                    // Something like
                    //    ls | notepad | sort.exe
                    // should block until the notepad process is terminated.
                    _isRunningInBackground = false;
                }
                else
                {
                    _isRunningInBackground = true;
                    if (startInfo.UseShellExecute == false)
                    {
                        _isRunningInBackground = IsWindowsApplication(_nativeProcess.StartInfo.FileName);
                    }
                }

                try
                {
                    //If input is redirected, start input to process.
                    if (startInfo.RedirectStandardInput)
                    {
                        NativeCommandIOFormat inputFormat = NativeCommandIOFormat.Text;
                        if (_isMiniShell)
                        {
                            inputFormat = ((MinishellParameterBinderController)NativeParameterBinderController).InputFormat;
                        }
                        lock (_sync)
                        {
                            if (!_stopped)
                            {
                                _inputWriter.Start(_nativeProcess, inputFormat);
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    StopProcessing();
                    throw;
                }

                if (_isRunningInBackground == false)
                {
                    InitOutputQueue();
                }
            }
            catch (Win32Exception e)
            {
                exceptionToRethrow = e;

            } // try
            catch (PipelineStoppedException)
            {
                // If we're stopping the process, just rethrow this exception...
                throw;
            }
            catch (Exception e)
            {
                exceptionToRethrow = e;
            }

            // An exception was thrown while attempting to run the program
            // so wrap and rethrow it here...
            if (exceptionToRethrow != null)
            {
                // It's a system exception so wrap it in one of ours and re-throw.
                string message = StringUtil.Format(ParserStrings.ProgramFailedToExecute,
                    this.NativeCommandName, exceptionToRethrow.Message,
                    this.Command.MyInvocation.PositionMessage);
                ApplicationFailedException appFailedException = new ApplicationFailedException(message, exceptionToRethrow);

                // There is no need to set this exception here since this exception will eventually be caught by pipeline processor.
                // this.commandRuntime.PipelineProcessor.ExecutionFailed = true;

                throw appFailedException;
            }
        }

        private void InitOutputQueue()
        {
            //if output is redirected, start reading output of process in queue.
            if (_nativeProcess.StartInfo.RedirectStandardOutput || _nativeProcess.StartInfo.RedirectStandardError)
            {
                lock (_sync)
                {
                    if (!_stopped)
                    {
                        _nativeProcessOutputQueue = new BlockingCollection<ProcessOutputObject>();
                        // we don't assign the handler to anything, because it's used only for objects marshaling
                        new ProcessOutputHandler(_nativeProcess, _nativeProcessOutputQueue);
                    }
                }
            }
        }

        private ProcessOutputObject DequeueProcessOutput(bool blocking)
        {
            if (blocking)
            {
                // If adding was completed and collection is empty (IsCompleted == true)
                // there is no need to do a blocking Take(), we should just return.
                if (!_nativeProcessOutputQueue.IsCompleted)
                {
                    try
                    {
                        // If adding is not complete we need a try {} catch {}
                        // to mitigate a concurrent call to CompleteAdding().
                        return _nativeProcessOutputQueue.Take();
                    }
                    catch (InvalidOperationException)
                    {
                        // It's a normal situation: another thread can mark collection as CompleteAdding
                        // in a concurrent way and we will rise an exception in Take().
                        // Although it's a normal situation it's not the most common path
                        // and will be executed only on the race condtion case.
                    }
                }

                return null;
            }
            else
            {
                ProcessOutputObject record = null;
                _nativeProcessOutputQueue.TryTake(out record);
                return record;
            }
        }

        /// Read the output from the native process and send it down the line.
        private void ConsumeAvailableNativeProcessOutput(bool blocking)
        {
            if (_isRunningInBackground == false)
            {
                if (_nativeProcess.StartInfo.RedirectStandardOutput || _nativeProcess.StartInfo.RedirectStandardError)
                {
                    ProcessOutputObject record;
                    while ((record = DequeueProcessOutput(blocking)) != null)
                    {
                        if (this.Command.Context.CurrentPipelineStopping)
                        {
                            this.StopProcessing();
                            return;
                        }

                        ProcessOutputRecord(record);
                    }
                }
            }
        }

        override void Complete()
        {
            Exception exceptionToRethrow = null;
            try
            {
                if (_isRunningInBackground == false)
                {
                    //Wait for input writer to finish.
                    _inputWriter.Done();

                    // read all the available output in the blocking way
                    ConsumeAvailableNativeProcessOutput(blocking: true);
                    _nativeProcess.WaitForExit();

                    // Capture screen output if we are transcribing
                    if (this.Command.Context.EngineHostInterface.UI.IsTranscribing &&
                        _scrapeHostOutput)
                    {
                        Host.Coordinates endPosition = this.Command.Context.EngineHostInterface.UI.RawUI.CursorPosition;
                        endPosition.X = this.Command.Context.EngineHostInterface.UI.RawUI.BufferSize.Width - 1;

                        // If the end position is before the start position, then capture the entire buffer.
                        if (endPosition.Y < _startPosition.Y)
                        {
                            _startPosition.Y = 0;
                        }

                        Host.BufferCell[,] bufferContents = this.Command.Context.EngineHostInterface.UI.RawUI.GetBufferContents(
                            new Host.Rectangle(_startPosition, endPosition));

                        StringBuilder lineContents = new StringBuilder();
                        StringBuilder bufferText = new StringBuilder();

                        for (int row = 0; row < bufferContents.GetLength(0); row++)
                        {
                            if (row > 0)
                            {
                                bufferText.Append(Environment.NewLine);
                            }

                            lineContents.Clear();
                            for (int column = 0; column < bufferContents.GetLength(1); column++)
                            {
                                lineContents.Append(bufferContents[row, column].Character);
                            }

                            bufferText.Append(lineContents.ToString().TrimEnd(Utils.Separators.SpaceOrTab));
                        }

                        this.Command.Context.InternalHost.UI.TranscribeResult(bufferText.ToString());
                    }

                    this.Command.Context.SetVariable(SpecialVariables.LastExitCodeVarPath, _nativeProcess.ExitCode);
                    if (_nativeProcess.ExitCode != 0)
                        this.commandRuntime.PipelineProcessor.ExecutionFailed = true;
                }
            }
            catch (Win32Exception e)
            {
                exceptionToRethrow = e;
            } // try
            catch (PipelineStoppedException)
            {
                // If we're stopping the process, just rethrow this exception...
                throw;
            }
            catch (Exception e)
            {
                exceptionToRethrow = e;
            }
            finally
            {
                if (!_nativeProcess.StartInfo.RedirectStandardOutput)
                {
                    this.Command.Context.EngineHostInterface.NotifyEndApplication();
                }
                // Do the clean up...
                CleanUp();
            }

            // An exception was thrown while attempting to run the program
            // so wrap and rethrow it here...
            if (exceptionToRethrow != null)
            {
                // It's a system exception so wrap it in one of ours and re-throw.
                string message = StringUtil.Format(ParserStrings.ProgramFailedToExecute,
                    this.NativeCommandName, exceptionToRethrow.Message,
                    this.Command.MyInvocation.PositionMessage);
                ApplicationFailedException appFailedException = new ApplicationFailedException(message, exceptionToRethrow);

                // There is no need to set this exception here since this exception will eventually be caught by pipeline processor.
                // this.commandRuntime.PipelineProcessor.ExecutionFailed = true;

                throw appFailedException;
            }
        }


        #region Process cleanup with Child Process cleanup

        /// Utility routine to kill a process, discarding non-critical exceptions.
        /// This utility makes two passes at killing a process. In the first pass,
        /// if the process handle is invalid (as seems to be the case with an ntvdm)
        /// then we try to get a fresh handle based on the original process id.
        /// <param name="processToKill">The process to kill</param>
        private static void KillProcess(Process processToKill)
        {
            if (NativeCommandProcessor.IsServerSide)
            {
                Process[] currentlyRunningProcs = Process.GetProcesses();
                ProcessWithParentId[] procsWithParentId = ProcessWithParentId.Construct(currentlyRunningProcs);
                KillProcessAndChildProcesses(processToKill, procsWithParentId);
                return;
            }

            try
            {
                processToKill.Kill();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                try
                {
                    // For processes running in an NTVDM, trying to kill with
                    // the original handle fails with a Win32 error, so we'll
                    // use the ID and try to get a new handle...
                    Process newHandle = Process.GetProcessById(processToKill.Id);
                    // If the process was not found, we won't get here...
                    newHandle.Kill();
                }
                catch (Exception)
                {
                }
            }
            catch (Exception)
            {
            }
        }

        /// Used by remote server to kill a process tree given
        /// a process id. Process class does not have ParentId
        /// property, so this wrapper uses WMI to get ParentId
        /// and wraps the original process.
        struct ProcessWithParentId
        {
            public Process OriginalProcessInstance;
            private int _parentId;
            public int ParentId
            {
                get
                {
                    // Construct parent id only once.
                    if (int.MinValue == _parentId)
                    {
                        ConstructParentId();
                    }
                    return _parentId;
                }
            }

            public ProcessWithParentId(Process originalProcess)
            {
                OriginalProcessInstance = originalProcess;
                _parentId = int.MinValue;
            }

            public static ProcessWithParentId[] Construct(Process[] originalProcCollection)
            {
                ProcessWithParentId[] result = new ProcessWithParentId[originalProcCollection.Length];
                for (int index = 0; index < originalProcCollection.Length; index++)
                {
                    result[index] = new ProcessWithParentId(originalProcCollection[index]);
                }
                return result;
            }

            private void ConstructParentId()
            {
                try
                {
                    // note that we have tried to retrieved parent id once.
                    // retrieving parent id might throw exceptions..so
                    // setting this to -1 so that we dont try again to
                    // get the parent id.
                    _parentId = -1;

                    Process parentProcess = PsUtils.GetParentProcess(OriginalProcessInstance);
                    if (parentProcess != null)
                    {
                        _parentId = parentProcess.Id;
                    }
                }
                catch (Win32Exception)
                {
                }
                catch (InvalidOperationException)
                {
                }
                catch (Microsoft.Management.Infrastructure.CimException)
                {
                }
            }
        }

        /// Kills the process tree (process + associated child processes)
        /// <param name="processToKill"></param>
        /// <param name="currentlyRunningProcs"></param>
        private static void KillProcessAndChildProcesses(Process processToKill,
            ProcessWithParentId[] currentlyRunningProcs)
        {
            try
            {
                // Kill children first..
                int processId = processToKill.Id;
                KillChildProcesses(processId, currentlyRunningProcs);

                // kill the parent after children terminated.
                processToKill.Kill();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                try
                {
                    // For processes running in an NTVDM, trying to kill with
                    // the original handle fails with a Win32 error, so we'll
                    // use the ID and try to get a new handle...
                    Process newHandle = Process.GetProcessById(processToKill.Id);

                    // If the process was not found, we won't get here...
                    newHandle.Kill();
                }
                catch (Exception)
                {
                }
            }
            catch (Exception)
            {
            }
        }

        private static void KillChildProcesses(int parentId, ProcessWithParentId[] currentlyRunningProcs)
        {
            foreach (ProcessWithParentId proc in currentlyRunningProcs)
            {
                if ((proc.ParentId > 0) && (proc.ParentId == parentId))
                {
                    KillProcessAndChildProcesses(proc.OriginalProcessInstance, currentlyRunningProcs);
                }
            }
        }

        #endregion

        #region checkForConsoleApplication

        /// Return true if the passed in process is a console process.
        /// <param name="fileName"></param>
        /// <returns></returns>
        private static bool IsConsoleApplication(string fileName)
        {
            return !IsWindowsApplication(fileName);
        }

        /// Check if the passed in process is a windows application.
        /// <param name="fileName"></param>
        /// <returns></returns>
        [ArchitectureSensitive]
        private static bool IsWindowsApplication(string fileName)
        {
#if UNIX
            return false;
#else
            if (Platform.IsNanoServer)
                return false;

            SHFILEINFO shinfo = new SHFILEINFO();
            IntPtr type = SHGetFileInfo(fileName, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_EXETYPE);

            switch ((int)type)
            {
                case 0x0:
                    // 0x0 = not an exe
                    return false;
                case 0x5a4d:
                    // 0x5a4d - DOS .exe or .com file
                    return false;
                case 0x4550:
                    // 0x4550 - windows console app or bat file
                    return false;
                default:
                    // anything else - is a windows program...
                    return true;
            }
#endif
        }

        #endregion checkForConsoleApplication

        /// This is set to true when StopProcessing is called
        private bool _stopped = false;
        /// Routine used to stop this processing on this node...
        void StopProcessing()
        {
            lock (_sync)
            {
                if (_stopped) return;
                _stopped = true;
            }

            if (_nativeProcess != null)
            {
                if (!_runStandAlone)
                {
                    //Stop input writer
                    _inputWriter.Stop();

                    KillProcess(_nativeProcess);
                }
            }
        }

        /// Aggressively clean everything up...
        private void CleanUp()
        {
            try
            {
                if (_nativeProcess != null)
                {
                    _nativeProcess.Dispose();
                }
            }
            catch (Exception)
            {
            }
        }

        private void ProcessOutputRecord(ProcessOutputObject outputValue)
        {
            Dbg.Assert(outputValue != null, "only object of type ProcessOutputObject expected");

            if (outputValue.Stream == MinishellStream.Error)
            {
                ErrorRecord record = outputValue.Data as ErrorRecord;
                Dbg.Assert(record != null, "ProcessReader should ensure that data is ErrorRecord");
                record.SetInvocationInfo(this.Command.MyInvocation);
                this.commandRuntime._WriteErrorSkipAllowCheck(record, isNativeError: true);
            }
            else if (outputValue.Stream == MinishellStream.Output)
            {
                this.commandRuntime._WriteObjectSkipAllowCheck(outputValue.Data);
            }
            else if (outputValue.Stream == MinishellStream.Debug)
            {
                string temp = outputValue.Data as string;
                Dbg.Assert(temp != null, "ProcessReader should ensure that data is string");
                this.Command.PSHostInternal.UI.WriteDebugLine(temp);
            }
            else if (outputValue.Stream == MinishellStream.Verbose)
            {
                string temp = outputValue.Data as string;
                Dbg.Assert(temp != null, "ProcessReader should ensure that data is string");
                this.Command.PSHostInternal.UI.WriteVerboseLine(temp);
            }
            else if (outputValue.Stream == MinishellStream.Warning)
            {
                string temp = outputValue.Data as string;
                Dbg.Assert(temp != null, "ProcessReader should ensure that data is string");
                this.Command.PSHostInternal.UI.WriteWarningLine(temp);
            }
            else if (outputValue.Stream == MinishellStream.Progress)
            {
                PSObject temp = outputValue.Data as PSObject;
                if (temp != null)
                {
                    long sourceId = 0;
                    PSMemberInfo info = temp.Properties["SourceId"];
                    if (info != null)
                    {
                        sourceId = (long)info.Value;
                    }
                    info = temp.Properties["Record"];
                    ProgressRecord rec = null;
                    if (info != null)
                    {
                        rec = info.Value as ProgressRecord;
                    }
                    if (rec != null)
                    {
                        this.Command.PSHostInternal.UI.WriteProgress(sourceId, rec);
                    }
                }
            }
            else if (outputValue.Stream == MinishellStream.Information)
            {
                InformationRecord record = outputValue.Data as InformationRecord;
                Dbg.Assert(record != null, "ProcessReader should ensure that data is InformationRecord");
                this.commandRuntime.WriteInformation(record);
            }
        }

        /// Gets the start info for process
        /// <param name="redirectOutput"></param>
        /// <param name="redirectError"></param>
        /// <param name="redirectInput"></param>
        /// <param name="soloCommand"></param>
        /// <returns></returns>
        private ProcessStartInfo GetProcessStartInfo(bool redirectOutput, bool redirectError, bool redirectInput, bool soloCommand)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = this.Path;

            // On Windows, check the extension list and see if we should try to execute this directly.
            // Otherwise, use the platform library to check executability
            if ((Platform.IsWindows && ValidateExtension(this.Path))
                || (!Platform.IsWindows && Platform.NonWindowsIsExecutable(this.Path)))
            {
                startInfo.UseShellExecute = false;
                if (redirectInput)
                {
                    startInfo.RedirectStandardInput = true;
                }
                if (redirectOutput)
                {
                    startInfo.RedirectStandardOutput = true;
                }
                if (redirectError)
                {
                    startInfo.RedirectStandardError = true;
                }
            }
            else
            {
                if(coreClr) {
                    // Shell doesn't exist on OneCore, so documents cannot be associated with an application.
                    // Therefore, we cannot run document directly on OneCore.
                    throw InterpreterError.NewInterpreterException(this.Path, typeof(RuntimeException),
                        this.Command.InvocationExtent, "CantActivateDocumentInPowerShellCore", ParserStrings.CantActivateDocumentInPowerShellCore, this.Path);
                } else {
                    // We only want to ShellExecute something that is standalone...
                    if (!soloCommand)
                    {
                        throw InterpreterError.NewInterpreterException(this.Path, typeof(RuntimeException),
                            this.Command.InvocationExtent, "CantActivateDocumentInPipeline", ParserStrings.CantActivateDocumentInPipeline, this.Path);
                    }

                    startInfo.UseShellExecute = true;
                }
            }

            startInfo.Arguments = NativeParameterBinderController.Arguments;

            ExecutionContext context = this.Command.Context;

            // Start command in the current filesystem directory
            string rawPath =
                context.EngineSessionState.GetNamespaceCurrentLocation(
                    context.ProviderNames.FileSystem).ProviderPath;
            startInfo.WorkingDirectory = WildcardPattern.Unescape(rawPath);
            return startInfo;
        }

        private bool IsDownstreamOutDefault(Pipe downstreamPipe)
        {
            return false;
        }

        /// This method calculates if input and output of the process are redirected
        /// <param name="redirectOutput"></param>
        /// <param name="redirectError"></param>
        /// <param name="redirectInput"></param>
        private void CalculateIORedirection(out bool redirectOutput, out bool redirectError, out bool redirectInput)
        {
            // TODO always redirect input?
            redirectInput = this.Command.MyInvocation.ExpectingInput;
            redirectOutput = true;
            redirectError = true;
        }

        private bool ValidateExtension(string path)
        {
            // Now check the extension and see if it's one of the ones in pathext
            string myExtension = System.IO.Path.GetExtension(path);

            string pathext = (string)LanguagePrimitives.ConvertTo(
                this.Command.Context.GetVariableValue(SpecialVariables.PathExtVarPath),
                typeof(string), CultureInfo.InvariantCulture);
            string[] extensionList;
            if (String.IsNullOrEmpty(pathext))
            {
                extensionList = new string[] { ".exe", ".com", ".bat", ".cmd" };
            }
            else
            {
                extensionList = pathext.Split(Utils.Separators.Semicolon);
            }
            foreach (string extension in extensionList)
            {
                if (String.Equals(extension, myExtension, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

#if !UNIX // Shell doesn't exist on OneCore, so documents cannot be associated with applications.
        #region Interop for FindExecutable...

        // Constant used to determine the buffer size for a path
        // when looking for an executable. MAX_PATH is defined as 260
        // so this is much larger than what should be permitted
        private const int MaxExecutablePath = 1024;

        // The FindExecutable API is defined in shellapi.h as
        // SHSTDAPI_(HINSTANCE) FindExecutableW(LPCWSTR lpFile, LPCWSTR lpDirectory, __out_ecount(MAX_PATH) LPWSTR lpResult);
        // HINSTANCE is void* so we need to use IntPtr as API return value.

        [DllImport("shell32.dll", EntryPoint = "FindExecutable")]
        [SuppressMessage("Microsoft.Globalization", "CA2101:SpecifyMarshalingForPInvokeStringArguments", MessageId = "0")]
        [SuppressMessage("Microsoft.Globalization", "CA2101:SpecifyMarshalingForPInvokeStringArguments", MessageId = "1")]
        [SuppressMessage("Microsoft.Globalization", "CA2101:SpecifyMarshalingForPInvokeStringArguments", MessageId = "2")]
        private static extern IntPtr FindExecutableW(
          string fileName, string directoryPath, StringBuilder pathFound);

        [ArchitectureSensitive]
        private static string FindExecutable(string filename)
        {
            // Preallocate a
            StringBuilder objResultBuffer = new StringBuilder(MaxExecutablePath);
            IntPtr resultCode = (IntPtr)0;

            try
            {
                resultCode = FindExecutableW(filename, string.Empty, objResultBuffer);
            }
            catch (System.IndexOutOfRangeException e)
            {
                // If we got an index-out-of-range exception here, it's because
                // of a buffer overrun error so we fail fast instead of
                // continuing to run in an possibly unstable environment....
                Environment.FailFast(e.Message, e);
            }

            // If FindExecutable returns a result >= 32, then it succeeded
            // and we return the string that was found, otherwise we
            // return null.
            if ((long)resultCode >= 32)
            {
                return objResultBuffer.ToString();
            }

            return null;
        }

        #endregion

        #region Interop for SHGetFileInfo

        private const int SCS_32BIT_BINARY = 0;  // A 32-bit Windows-based application
        private const int SCS_DOS_BINARY = 1;  // An MS-DOS - based application
        private const int SCS_WOW_BINARY = 2;  // A 16-bit Windows-based application
        private const int SCS_PIF_BINARY = 3;  // A PIF file that executes an MS-DOS - based application
        private const int SCS_POSIX_BINARY = 4;  // A POSIX - based application
        private const int SCS_OS216_BINARY = 5;  // A 16-bit OS/2-based application
        private const int SCS_64BIT_BINARY = 6;  // A 64-bit Windows-based application.

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        };

        private const uint SHGFI_EXETYPE = 0x000002000; // flag used to ask to return exe type

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        #endregion
#endif

        internal static bool IsServerSide { get; set; }
    }

    internal class ProcessOutputHandler
    {
        private int _refCount;
        private BlockingCollection<ProcessOutputObject> _queue;
        private bool _isFirstOutput;
        private bool _isFirstError;
        private bool _isXmlCliOutput;
        private bool _isXmlCliError;
        private string _processFileName;

        public ProcessOutputHandler(Process process, BlockingCollection<ProcessOutputObject> queue)
        {
            Debug.Assert(process.StartInfo.RedirectStandardOutput || process.StartInfo.RedirectStandardError, "Caller should redirect at least one stream");
            _refCount = 0;
            _processFileName = process.StartInfo.FileName;
            _queue = queue;

            // we incrementing refCount on the same thread and before running any processing
            // so it's safe to do it without Interlocked.
            if (process.StartInfo.RedirectStandardOutput) { _refCount++; }
            if (process.StartInfo.RedirectStandardError) { _refCount++; }

            // once we have _refCount, we can start processing
            if (process.StartInfo.RedirectStandardOutput)
            {
                _isFirstOutput = true;
                _isXmlCliOutput = false;
                process.OutputDataReceived += new DataReceivedEventHandler(OutputHandler);
                process.BeginOutputReadLine();
            }

            if (process.StartInfo.RedirectStandardError)
            {
                _isFirstError = true;
                _isXmlCliError = false;
                process.ErrorDataReceived += new DataReceivedEventHandler(ErrorHandler);
                process.BeginErrorReadLine();
            }
        }

        private void decrementRefCount()
        {
            Debug.Assert(_refCount > 0, "RefCount should always be positive, when we are trying to decrement it");
            if (Interlocked.Decrement(ref _refCount) == 0)
            {
                _queue.CompleteAdding();
            }
        }

        private void OutputHandler(object sender, DataReceivedEventArgs outputReceived)
        {
            if (outputReceived.Data != null)
            {
                if (_isFirstOutput)
                {
                    _isFirstOutput = false;
                    if (string.Equals(outputReceived.Data, XmlCliTag, StringComparison.Ordinal))
                    {
                        _isXmlCliOutput = true;
                        return;
                    }
                }

                if (_isXmlCliOutput)
                {
                    foreach (var record in DeserializeCliXmlObject(outputReceived.Data, true))
                    {
                        _queue.Add(record);
                    }
                }
                else
                {
                    _queue.Add(new ProcessOutputObject(outputReceived.Data, MinishellStream.Output));
                }
            }
            else
            {
                decrementRefCount();
            }
        }

        private void ErrorHandler(object sender, DataReceivedEventArgs errorReceived)
        {
            if (errorReceived.Data != null)
            {
                if (string.Equals(errorReceived.Data, XmlCliTag, StringComparison.Ordinal))
                {
                    _isXmlCliError = true;
                    return;
                }

                if (_isXmlCliError)
                {
                    foreach (var record in DeserializeCliXmlObject(errorReceived.Data, false))
                    {
                        _queue.Add(record);
                    }
                }
                else
                {
                    ErrorRecord errorRecord;
                    if (_isFirstError)
                    {
                        _isFirstError = false;
                        // Produce a regular error record for the first line of the output
                        errorRecord = new ErrorRecord(new RemoteException(errorReceived.Data), "NativeCommandError", ErrorCategory.NotSpecified, errorReceived.Data);
                    }
                    else
                    {
                        // Wrap the rest of the output in ErrorRecords with the "NativeCommandErrorMessage" error ID
                        errorRecord = new ErrorRecord(new RemoteException(errorReceived.Data), "NativeCommandErrorMessage", ErrorCategory.NotSpecified, null);
                    }

                    _queue.Add(new ProcessOutputObject(errorRecord, MinishellStream.Error));
                }
            }
            else
            {
                decrementRefCount();
            }
        }

        private List<ProcessOutputObject> DeserializeCliXmlObject(string xml, bool isOutput)
        {
            var result = new List<ProcessOutputObject>();
            try
            {
                using (var streamReader = new MemoryStream(Encoding.UTF8.GetBytes(xml)))
                {
                    XmlReader xmlReader = XmlReader.Create(streamReader, InternalDeserializer.XmlReaderSettingsForCliXml);
                    Deserializer des = new Deserializer(xmlReader);
                    while (!des.Done())
                    {
                        string streamName;
                        object obj = des.Deserialize(out streamName);

                        //Decide the stream to which data belongs
                        MinishellStream stream = MinishellStream.Unknown;
                        if (streamName != null)
                        {
                            stream = StringToMinishellStreamConverter.ToMinishellStream(streamName);
                        }
                        if (stream == MinishellStream.Unknown)
                        {
                            stream = isOutput ? MinishellStream.Output : MinishellStream.Error;
                        }

                        //Null is allowed only in output stream
                        if (stream != MinishellStream.Output && obj == null)
                        {
                            continue;
                        }

                        if (stream == MinishellStream.Error)
                        {
                            if (obj is PSObject)
                            {
                                obj = ErrorRecord.FromPSObjectForRemoting(PSObject.AsPSObject(obj));
                            }
                            else
                            {
                                string errorMessage = null;
                                try
                                {
                                    errorMessage = (string)LanguagePrimitives.ConvertTo(obj, typeof(string), CultureInfo.InvariantCulture);
                                }
                                catch (PSInvalidCastException)
                                {
                                    continue;
                                }
                                obj = new ErrorRecord(new RemoteException(errorMessage),
                                                    "NativeCommandError", ErrorCategory.NotSpecified, errorMessage);
                            }
                        }
                        else if (stream == MinishellStream.Information)
                        {
                            if (obj is PSObject)
                            {
                                obj = InformationRecord.FromPSObjectForRemoting(PSObject.AsPSObject(obj));
                            }
                            else
                            {
                                string messageData = null;
                                try
                                {
                                    messageData = (string)LanguagePrimitives.ConvertTo(obj, typeof(string), CultureInfo.InvariantCulture);
                                }
                                catch (PSInvalidCastException)
                                {
                                    continue;
                                }

                                obj = new InformationRecord(messageData, null);
                            }
                        }
                        else if (stream == MinishellStream.Debug ||
                                 stream == MinishellStream.Verbose ||
                                 stream == MinishellStream.Warning)
                        {
                            //Convert to string
                            try
                            {
                                obj = LanguagePrimitives.ConvertTo(obj, typeof(string), CultureInfo.InvariantCulture);
                            }
                            catch (PSInvalidCastException)
                            {
                                continue;
                            }
                        }
                        result.Add(new ProcessOutputObject(obj, stream));
                    }
                }
            }
            catch (XmlException originalException)
            {
                string template = NativeCP.CliXmlError;
                string message = string.Format(
                    null,
                    template,
                    isOutput ? MinishellStream.Output : MinishellStream.Error,
                    _processFileName,
                    originalException.Message);
                XmlException newException = new XmlException(
                    message,
                    originalException);

                ErrorRecord error = new ErrorRecord(
                    newException,
                    "ProcessStreamReader_CliXmlError",
                    ErrorCategory.SyntaxError,
                    _processFileName);
                result.Add(new ProcessOutputObject(error, MinishellStream.Error));
            }

            return result;
        }
    }

    /// Helper class to handle writing input to a process.
    internal class ProcessInputWriter
    {
        #region constructor

        private InternalCommand _command;
        /// Creates an instance of ProcessInputWriter
        internal ProcessInputWriter(InternalCommand command)
        {
            Dbg.Assert(command != null, "Caller should validate the parameter");
            _command = command;
        }

        #endregion constructor

        private SteppablePipeline _pipeline;
        private Serializer _xmlSerializer;

        /// Add an object to write to process
        /// <param name="input"></param>
        internal void Add(object input)
        {
            if (_stopping || _streamWriter == null)
            {
                // if _streamWriter is already null, then we already called Dispose()
                // so we should just discard the input.
                return;
            }

            if (_inputFormat == NativeCommandIOFormat.Text)
            {
                AddTextInput(input);
            }
            else // Xml
            {
                AddXmlInput(input);
            }
        }

        private void AddTextInput(object input)
        {
            AddTextInputFromFormattedArray(_pipeline.Process(input));
        }

        private void AddTextInputFromFormattedArray(Array formattedObjects)
        {
            foreach (var item in formattedObjects)
            {
                string line = PSObject.ToStringParser(_command.Context, item);
                // if process is already finished and we are trying to write something to it,
                // we will get IOException
                try
                {
                    _streamWriter.WriteLine(line);
                }
                catch (IOException)
                {
                    // we are assuming that process is already finished
                    // we should just stop processing at this point
                    this.Dispose();
                    // stop foreach execution
                    break;
                }
            }
        }

        private void AddXmlInput(object input)
        {
            try
            {
                _xmlSerializer.Serialize(input);
            }
            catch (IOException)
            {
                // we are assuming that process is already finished
                // we should just stop processing at this point
                this.Dispose();
            }
        }

        /// Stream to which input is written
        private StreamWriter _streamWriter;

        /// Format of input.
        private NativeCommandIOFormat _inputFormat;

        /// Start writing input to process
        /// <param name="process">
        /// process to which input is written
        /// </param>
        /// <param name="inputFormat">
        /// </param>
        internal void Start(Process process, NativeCommandIOFormat inputFormat)
        {
            Dbg.Assert(process != null, "caller should validate the paramter");

            //Get the encoding for writing to native command. Note we get the Encoding
            //from the current scope so a script or function can use a different encoding
            //than global value.
            // TODO allow override via local variable / parameter
            Encoding pipeEncoding = _command.Context.GetVariableValue(SpecialVariables.OutputEncodingVarPath) as System.Text.Encoding ??
                                    Encoding.ASCII;

            _streamWriter = new StreamWriter(process.StandardInput.BaseStream, pipeEncoding);
            _streamWriter.AutoFlush = true;

            _inputFormat = inputFormat;

            if (_inputFormat == NativeCommandIOFormat.Xml)
            {
                _streamWriter.WriteLine(ProcessOutputHandler.XmlCliTag);
                _xmlSerializer = new Serializer(XmlWriter.Create(_streamWriter));
            }
            else // Text
            {
                _pipeline = ScriptBlock.Create("Out-String -Stream").GetSteppablePipeline();
                _pipeline.Begin(true);
            }
        }

        bool _stopping = false;

        /// Stop writing input to process
        internal void Stop()
        {
            _stopping = true;
        }

        internal void Dispose()
        {
            // we allow call Dispose() multiply times.
            // For example one time from ProcessRecord() code path,
            // when we detect that process already finished
            // and once from Done() code path.
            // Even though Dispose() could be called multiple times,
            // the calls are on the same thread, so there is no race condition
            if (_pipeline != null)
            {
                _pipeline.Dispose();
                _pipeline = null;
            }

            if (_xmlSerializer != null)
            {
                _xmlSerializer = null;
            }

            // streamWriter can be null if we didn't call Start method
            if (_streamWriter != null)
            {
                try
                {
                    _streamWriter.Dispose();
                }
                catch (IOException)
                {
                    // on unix, if process is already finished attempt to dispose it will
                    // lead to "Broken pipe" exception.
                    // we are ignoring it here
                }
                _streamWriter = null;
            }
        }

        internal void Done()
        {
            if (_inputFormat == NativeCommandIOFormat.Xml)
            {
                if (_xmlSerializer != null)
                {
                    _xmlSerializer.Done();
                }
            }
            else // Text
            {
                // if _pipeline == null, we already called Dispose(),
                // for example, because downstream process finished
                if (_pipeline != null)
                {
                    var finalResults = _pipeline.End();
                    AddTextInputFromFormattedArray(finalResults);
                }
            }

            Dispose();
        }
    }

#if !CORECLR // There is no GUI application on OneCore, so powershell on OneCore should always have a console attached.

    /// Static class that allows you to show and hide the console window
    /// associated with this process.
    internal static class ConsoleVisibility
    {
        /// If set to true, then native commands will always be run redirected...
        public static bool AlwaysCaptureApplicationIO { get; set; }

        [DllImport("Kernel32.dll")]
        internal static extern IntPtr GetConsoleWindow();

        internal const int SW_HIDE = 0;
        internal const int SW_SHOWNORMAL = 1;
        internal const int SW_NORMAL = 1;
        internal const int SW_SHOWMINIMIZED = 2;
        internal const int SW_SHOWMAXIMIZED = 3;
        internal const int SW_MAXIMIZE = 3;
        internal const int SW_SHOWNOACTIVATE = 4;
        internal const int SW_SHOW = 5;
        internal const int SW_MINIMIZE = 6;
        internal const int SW_SHOWMINNOACTIVE = 7;
        internal const int SW_SHOWNA = 8;
        internal const int SW_RESTORE = 9;
        internal const int SW_SHOWDEFAULT = 10;
        internal const int SW_FORCEMINIMIZE = 11;
        internal const int SW_MAX = 11;

        /// Code to control the display properties of the a window...
        /// <param name="hWnd">The window to show...</param>
        /// <param name="nCmdShow">The command to do</param>
        /// <returns>true it it was successful</returns>
        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, Int32 nCmdShow);

        /// Code to allocate a console...
        /// <returns>true if a console was created...</returns>
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool AllocConsole();


        /// Called to save the foreground window before allocating a hidden console window
        /// <returns>A handle to the foreground window</returns>
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        /// Called to restore the foreground window after allocating a hidden console window
        /// <param name="hWnd">A handle to the window that should be activated and brought to the foreground.</param>
        /// <returns>true if the window was brought to the foreground</returns>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        /// If no console window is attached to this process, then allocate one,
        /// hide it and return true. If there's already a console window attached, then
        /// just return false.
        /// <returns></returns>
        internal static bool AllocateHiddenConsole()
        {
            // See if there is already a console attached.
            IntPtr hwnd = ConsoleVisibility.GetConsoleWindow();
            if (hwnd != IntPtr.Zero)
            {
                return false;
            }

            // save the foreground window since allocating a console window might remove focus from it
            IntPtr savedForeground = ConsoleVisibility.GetForegroundWindow();

            // Since there is no console window, allocate and then hide it...
            // Suppress the PreFAST warning about not using Marshal.GetLastWin32Error() to
            // get the error code.
#pragma warning disable 56523
            ConsoleVisibility.AllocConsole();
            hwnd = ConsoleVisibility.GetConsoleWindow();

            bool returnValue;
            if (hwnd == IntPtr.Zero)
            {
                returnValue = false;
            }
            else
            {
                returnValue = true;
                ConsoleVisibility.ShowWindow(hwnd, ConsoleVisibility.SW_HIDE);
                AlwaysCaptureApplicationIO = true;
            }

            if (savedForeground != IntPtr.Zero && ConsoleVisibility.GetForegroundWindow() != savedForeground)
            {
                ConsoleVisibility.SetForegroundWindow(savedForeground);
            }

            return returnValue;
        }

        /// If there is a console attached, then make it visible
        /// and allow interactive console applications to be run.
        public static void Show()
        {
            IntPtr hwnd = GetConsoleWindow();
            if (hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, SW_SHOW);
                AlwaysCaptureApplicationIO = false;
            }
            else
            {
                throw PSTraceSource.NewInvalidOperationException();
            }
        }

        /// If there is a console attached, then hide it and always capture
        /// output from the child process.
        public static void Hide()
        {
            IntPtr hwnd = GetConsoleWindow();
            if (hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, SW_HIDE);
                AlwaysCaptureApplicationIO = true;
            }
            else
            {
                throw PSTraceSource.NewInvalidOperationException();
            }
        }
    }
#endif

    /// Exception used to wrap the error coming from
    /// remote instance of Msh.
    /// <remarks>
    /// This remote instance of Msh can be in a separate process,
    /// appdomain or machine.
    /// </remarks>
    [Serializable]
    [SuppressMessage("Microsoft.Usage", "CA2240:ImplementISerializableCorrectly")]
    public class RemoteException : RuntimeException
    {
        /// Initializes a new instance of RemoteException
        public RemoteException()
            : base()
        {
        }

        /// Initializes a new instance of RemoteException with a specified error message.
        /// <param name="message">
        /// The message that describes the error.
        /// </param>
        public RemoteException(string message)
            : base(message)
        {
        }

        /// Initializes a new instance of the RemoteException class
        /// with a specified error message and a reference to the inner exception
        /// that is the cause of this exception.
        /// <param name="message">
        /// The message that describes the error.
        /// </param>
        /// <param name="innerException">
        /// The exception that is the cause of the current exception.
        /// </param>
        public RemoteException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        /// Initializes a new instance of the RemoteException
        /// with a specified error message, serialized Exception and
        /// serialized InvocationInfo
        /// <param name="message">The message that describes the error. </param>
        /// <param name="serializedRemoteException">
        /// serialized exception from remote msh
        /// </param>
        /// <param name="serializedRemoteInvocationInfo">
        /// serialized invocation info from remote msh
        /// </param>
        internal RemoteException
        (
            string message,
            PSObject serializedRemoteException,
            PSObject serializedRemoteInvocationInfo
        )
            : base(message)
        {
            _serializedRemoteException = serializedRemoteException;
            _serializedRemoteInvocationInfo = serializedRemoteInvocationInfo;
        }


        #region ISerializable Members

        /// Initializes a new instance of the <see cref="RemoteException"/>
        ///  class with serialized data.
        /// <param name="info">
        /// The <see cref="SerializationInfo"/> that holds the serialized object
        /// data about the exception being thrown.
        /// </param>
        /// <param name="context">
        /// The <see cref="StreamingContext"/> that contains contextual information
        /// about the source or destination.
        /// </param>
        protected RemoteException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }

        #endregion

        [NonSerialized]
        private PSObject _serializedRemoteException;
        [NonSerialized]
        private PSObject _serializedRemoteInvocationInfo;

        /// Original Serialized Exception from remote msh
        /// <remarks>This is the exception which was thrown in remote.
        /// </remarks>
        public PSObject SerializedRemoteException
        {
            get
            {
                return _serializedRemoteException;
            }
        }

        /// InvocationInfo, if any, associated with the SerializedRemoteException.
        /// <remarks>
        /// This is the serialized InvocationInfo from the remote msh.
        /// </remarks>
        public PSObject SerializedRemoteInvocationInfo
        {
            get
            {
                return _serializedRemoteInvocationInfo;
            }
        }

        private ErrorRecord _remoteErrorRecord;
        /// Sets the remote error record associated with this exception
        /// <param name="remoteError"></param>
        internal void SetRemoteErrorRecord(ErrorRecord remoteError)
        {
            _remoteErrorRecord = remoteError;
        }

        /// ErrorRecord associated with the exception
        public override ErrorRecord ErrorRecord
        {
            get
            {
                if (_remoteErrorRecord != null)
                    return _remoteErrorRecord;

                return base.ErrorRecord;
            }
        }
    }
}
