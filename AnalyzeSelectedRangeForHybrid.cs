using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using ScriptPortal.Vegas;

public class EntryPoint
{
    public void FromVegas(Vegas vegas)
    {
        string projectPath = vegas.Project.FilePath;
        if (String.IsNullOrEmpty(projectPath))
            throw new ApplicationException("Save the VEGAS project before running this script.");

        string projectDir = Path.GetDirectoryName(projectPath);
        long startFrame = vegas.SelectionStart.FrameCount;
        long lengthFrames = vegas.SelectionLength.FrameCount;
        if (lengthFrames <= 0)
            throw new ApplicationException("Select a timeline range first. Selection length is zero.");

        long endFrame = startFrame + lengthFrames;
        string projectBase = Path.GetFileNameWithoutExtension(projectPath);
        string settingsPath = Path.Combine(projectDir, projectBase + "-hybrid-settings.txt");
        string userSettingsPath = GetUserSettingsPath(projectBase);
        string sharedSettingsPath = GetSharedSettingsPath();
        string defaultOutputDir = LoadLastOutputDirectory(settingsPath, userSettingsPath, sharedSettingsPath, projectDir);
        string defaultTempDir = LoadLastTempDirectory(settingsPath, userSettingsPath, sharedSettingsPath, defaultOutputDir);
        string lastVideoTemplate = LoadSettingValue(settingsPath, userSettingsPath, sharedSettingsPath, "VideoTemplate");
        string lastAudioTemplate = LoadSettingValue(settingsPath, userSettingsPath, sharedSettingsPath, "AudioTemplate");

        double fps = vegas.Project.Video.FrameRate;
        List<VideoEventInfo> events = CollectVideoEvents(vegas, startFrame, endFrame);
        List<Range> complex = FindComplexRanges(vegas, events, startFrame, endFrame);
        complex = MergeRanges(complex);
        complex = PreserveUncopyableTimelineRanges(events, complex, startFrame, endFrame);
        List<SimpleSegment> previewSimple = BuildSimpleSegments(events, complex, startFrame, endFrame, fps,
            Path.Combine(defaultTempDir, projectBase + "-hybrid-range-" + startFrame + "-" + endFrame));

        RunSelection run = ShowSetupWindow(vegas, defaultTempDir, defaultOutputDir, lastVideoTemplate, lastAudioTemplate, projectBase, startFrame, endFrame, fps, complex, previewSimple);
        if (run == null)
            return;

        string tempRootDir = run.TempDirectory;
        string outputDir = run.OutputDirectory;
        Directory.CreateDirectory(tempRootDir);
        Directory.CreateDirectory(outputDir);
        SaveSettings(settingsPath, userSettingsPath, sharedSettingsPath, tempRootDir, outputDir, run.VideoTemplateKey, run.AudioTemplateKey);

        string outDir = Path.Combine(tempRootDir, projectBase + "-hybrid-range-" + startFrame + "-" + endFrame);
        bool resumeRun = Directory.Exists(outDir);
        Directory.CreateDirectory(outDir);

        List<SimpleSegment> simple = BuildSimpleSegments(events, complex, startFrame, endFrame, fps, outDir);
        string finalFile = MakeFinalPath(outputDir, projectBase, startFrame, endFrame, resumeRun);

        string complexCsv = Path.Combine(outDir, "vegas-complex-ranges.csv");
        string simpleCsv = Path.Combine(outDir, "ffmpeg-simple-segments.csv");
        string vegasRanges = Path.Combine(outDir, "vegas-ranges.txt");
        string workConfig = Path.Combine(tempRootDir, projectBase + "-hybrid-workdir.txt");
        string projectWorkConfig = Path.Combine(projectDir, projectBase + "-hybrid-workdir.txt");
        string ffmpegScript = Path.Combine(outDir, "render-simple-segments.ps1");
        string assembleScript = Path.Combine(outDir, "assemble-video-copy.ps1");
        string audioFile = Path.Combine(outDir, "hybrid_audio.wav");
        string muxScript = Path.Combine(outDir, "mux-final-with-audio.ps1");
        string summaryPath = Path.Combine(outDir, "summary.txt");

        WriteComplexCsv(complexCsv, complex, fps);
        WriteSimpleCsv(simpleCsv, simple);
        WriteVegasRanges(vegasRanges, complex);
        File.WriteAllText(workConfig, outDir, Encoding.UTF8);
        File.WriteAllText(projectWorkConfig, outDir, Encoding.UTF8);
        double expectedDurationSeconds = ((double)(endFrame - startFrame)) / fps;

        WriteFfmpegScript(ffmpegScript, simple, fps);
        WriteAssembleScript(assembleScript, simple, complex, fps, outDir);
        WriteMuxScript(muxScript, outDir, audioFile, finalFile, expectedDurationSeconds);
        WriteSummary(summaryPath, startFrame, endFrame, fps, complex, simple, complexCsv, simpleCsv, vegasRanges, workConfig, ffmpegScript, assembleScript, audioFile, muxScript, finalFile);

        ExecuteFullPipeline(vegas, run, outDir, tempRootDir, outputDir, fps, complex, simple, startFrame, endFrame, ffmpegScript, assembleScript, audioFile, muxScript, finalFile);
    }

    private string ChooseWorkDirectory(string initialDir)
    {
        using (FolderBrowserDialog dialog = new FolderBrowserDialog())
        {
            dialog.Description = "Choose a folder";
            dialog.SelectedPath = Directory.Exists(initialDir) ? initialDir : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            dialog.ShowNewFolderButton = true;
            return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null;
        }
    }

    private RunSelection ShowSetupWindow(Vegas vegas, string defaultTempDir, string defaultOutputDir, string lastVideoTemplate, string lastAudioTemplate, string projectBase, long startFrame, long endFrame, double fps,
        List<Range> complex, List<SimpleSegment> simple)
    {
        List<RenderChoice> videoChoices = BuildVideoRenderChoices(vegas);
        List<RenderChoice> audioChoices = BuildAudioRenderChoices(vegas);
        if (videoChoices.Count == 0)
            throw new ApplicationException("No valid video render templates found.");
        if (audioChoices.Count == 0)
            throw new ApplicationException("No valid audio render templates found.");

        RunSelection result = null;

        Form form = new Form();
        form.Text = "Hybrid render";
        form.StartPosition = FormStartPosition.CenterScreen;
        form.Width = 860;
        form.Height = 660;
        form.MinimizeBox = false;
        form.MaximizeBox = false;
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.BackColor = ColorFromRgb(32, 32, 32);
        form.ForeColor = ColorFromRgb(235, 235, 235);

        Panel header = new Panel();
        header.Left = 0;
        header.Top = 0;
        header.Width = 860;
        header.Height = 72;
        header.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
        header.BackColor = ColorFromRgb(24, 24, 24);
        form.Controls.Add(header);

        Label title = DarkLabel("Hybrid render", 18, 14, 360, 26);
        title.Font = new System.Drawing.Font("Segoe UI", 12.0f, System.Drawing.FontStyle.Bold);
        header.Controls.Add(title);

        Label summary = DarkLabel(
            "Selection " + startFrame.ToString(CultureInfo.InvariantCulture) + "-" + endFrame.ToString(CultureInfo.InvariantCulture) +
            " | VEGAS ranges: " + complex.Count.ToString(CultureInfo.InvariantCulture) +
            " | FFmpeg segments: " + simple.Count.ToString(CultureInfo.InvariantCulture) +
            " | " + Seconds(endFrame - startFrame, fps) + " sec",
            18, 42, 810, 22);
        summary.ForeColor = ColorFromRgb(175, 175, 175);
        header.Controls.Add(summary);

        Label videoLabel = DarkLabel("Video render template", 24, 94, 220, 22);
        form.Controls.Add(videoLabel);

        TextBox videoSearch = DarkTextBox(24, 118, 800, 26);
        form.Controls.Add(videoSearch);

        ComboBox videoCombo = DarkCombo(24, 150, 800, 28);
        RefreshVideoCombo(videoCombo, videoChoices, videoSearch.Text, lastVideoTemplate, vegas);
        videoSearch.TextChanged += delegate(object sender, EventArgs e)
        {
            string current = videoCombo.SelectedItem == null ? lastVideoTemplate : videoCombo.SelectedItem.ToString();
            RefreshVideoCombo(videoCombo, videoChoices, videoSearch.Text, current, vegas);
        };
        form.Controls.Add(videoCombo);

        Label audioLabel = DarkLabel("Audio render template", 24, 190, 220, 22);
        form.Controls.Add(audioLabel);

        TextBox audioSearch = DarkTextBox(24, 214, 800, 26);
        form.Controls.Add(audioSearch);

        ComboBox audioCombo = DarkCombo(24, 246, 800, 28);
        RefreshAudioCombo(audioCombo, audioChoices, audioSearch.Text, lastAudioTemplate);
        audioSearch.TextChanged += delegate(object sender, EventArgs e)
        {
            string current = audioCombo.SelectedItem == null ? lastAudioTemplate : audioCombo.SelectedItem.ToString();
            RefreshAudioCombo(audioCombo, audioChoices, audioSearch.Text, current);
        };
        form.Controls.Add(audioCombo);

        Label tempFolderLabel = DarkLabel("Temporary files folder", 24, 286, 220, 22);
        form.Controls.Add(tempFolderLabel);

        TextBox tempFolderText = DarkTextBox(24, 310, 724, 28);
        tempFolderText.Text = defaultTempDir;
        form.Controls.Add(tempFolderText);

        Button browseTemp = DarkButton("...", 758, 308, 66, 30, false);
        browseTemp.Click += delegate(object sender, EventArgs e)
        {
            string chosen = ChooseWorkDirectory(Directory.Exists(tempFolderText.Text) ? tempFolderText.Text : defaultTempDir);
            if (!String.IsNullOrEmpty(chosen))
                tempFolderText.Text = chosen;
        };
        form.Controls.Add(browseTemp);

        Label outputFolderLabel = DarkLabel("Final output folder", 24, 352, 220, 22);
        form.Controls.Add(outputFolderLabel);

        TextBox outputFolderText = DarkTextBox(24, 376, 724, 28);
        outputFolderText.Text = defaultOutputDir;
        form.Controls.Add(outputFolderText);

        Button browseOutput = DarkButton("...", 758, 374, 66, 30, false);
        browseOutput.Click += delegate(object sender, EventArgs e)
        {
            string chosen = ChooseWorkDirectory(Directory.Exists(outputFolderText.Text) ? outputFolderText.Text : defaultOutputDir);
            if (!String.IsNullOrEmpty(chosen))
                outputFolderText.Text = chosen;
        };
        form.Controls.Add(browseOutput);

        Label stageLabel = DarkLabel("Start stage", 24, 418, 220, 22);
        form.Controls.Add(stageLabel);

        ComboBox stageCombo = DarkCombo(24, 442, 800, 28);
        stageCombo.Items.Add(new StageChoice(-1, "Auto / continue from detected outputs"));
        stageCombo.Items.Add(new StageChoice(0, "1. VEGAS complex ranges"));
        stageCombo.Items.Add(new StageChoice(1, "2. FFmpeg simple ranges"));
        stageCombo.Items.Add(new StageChoice(2, "3. Assemble video"));
        stageCombo.Items.Add(new StageChoice(3, "4. Render audio WAV"));
        stageCombo.Items.Add(new StageChoice(4, "5. Mux final MP4"));
        stageCombo.Items.Add(new StageChoice(5, "6. Validate final MP4"));
        stageCombo.SelectedIndex = 0;
        form.Controls.Add(stageCombo);

        Label note = DarkLabel("Temporary files go into the temporary folder. Final MP4 and copied log go into the final output folder.", 24, 484, 800, 22);
        note.ForeColor = ColorFromRgb(170, 170, 170);
        form.Controls.Add(note);

        Button execute = DarkButton("Run", 624, 526, 200, 42, true);
        execute.Click += delegate(object sender, EventArgs e)
        {
            string tempFolder = tempFolderText.Text.Trim();
            string outputFolder = outputFolderText.Text.Trim();
            if (tempFolder.Length == 0)
            {
                MessageBox.Show("Choose a temporary files folder.", "Hybrid render", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (outputFolder.Length == 0)
            {
                MessageBox.Show("Choose a final output folder.", "Hybrid render", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (videoCombo.SelectedItem == null || audioCombo.SelectedItem == null)
            {
                MessageBox.Show("Choose video and audio render templates.", "Hybrid render", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                Directory.CreateDirectory(tempFolder);
                Directory.CreateDirectory(outputFolder);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Render folders", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            StageChoice stageChoice = stageCombo.SelectedItem as StageChoice;
            if (stageChoice == null)
                stageChoice = new StageChoice(-1, "Auto / continue from detected outputs");

            result = new RunSelection(tempFolder, outputFolder, (RenderChoice)videoCombo.SelectedItem, (RenderChoice)audioCombo.SelectedItem, stageChoice.StepIndex);
            form.DialogResult = DialogResult.OK;
            form.Close();
        };
        form.Controls.Add(execute);
        form.AcceptButton = execute;

        if (form.ShowDialog() != DialogResult.OK)
            return null;
        return result;
    }

    private void ExecuteFullPipeline(Vegas vegas, RunSelection run, string outDir, string tempRootDir, string outputDir, double fps, List<Range> complex, List<SimpleSegment> simple,
        long startFrame, long endFrame, string ffmpegScript, string assembleScript, string audioFile, string muxScript, string finalFile)
    {
        string logPath = Path.Combine(outDir, "hybrid-render.log");
        string statePath = Path.Combine(outDir, "hybrid-render-state.txt");
        string finalLogPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(finalFile) + ".log.txt");
        string videoOnlyFile = Path.Combine(outDir, "hybrid_video_only.mp4");
        double expectedDurationSeconds = ((double)(endFrame - startFrame)) / fps;
        string redirectState = Path.Combine(outDir, "hybrid-video-location.txt");
        if (File.Exists(redirectState))
        {
            string recorded = File.ReadAllText(redirectState).Trim();
            if (Path.IsPathRooted(recorded) && File.Exists(recorded))
                videoOnlyFile = recorded;
        }
        PipelineStep[] steps = BuildPipelineSteps(outDir, complex, simple,
            delegate { return videoOnlyFile; }, audioFile, delegate { return finalFile; }, fps, expectedDurationSeconds);
        int selectedStartStepIndex = run.StartStepIndex;
        if (selectedStartStepIndex < -1 || selectedStartStepIndex >= steps.Length)
            selectedStartStepIndex = -1;
        int totalFragments = complex.Count + simple.Count;

        Form form = new Form();
        form.Text = "Hybrid render progress";
        form.StartPosition = FormStartPosition.CenterScreen;
        form.Width = 980;
        form.Height = 680;
        form.MinimizeBox = false;
        form.MaximizeBox = false;
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.BackColor = ColorFromRgb(32, 32, 32);
        form.ForeColor = ColorFromRgb(235, 235, 235);

        Label title = DarkLabel("Hybrid render pipeline", 18, 14, 360, 26);
        title.Font = new Font("Segoe UI", 12.0f, FontStyle.Bold);
        form.Controls.Add(title);

        Label timer = DarkLabel("Elapsed 00:00 | Expected --:-- | Remaining --:--", 18, 42, 560, 22);
        timer.ForeColor = ColorFromRgb(180, 180, 180);
        form.Controls.Add(timer);

        Label fragmentLabel = DarkLabel("Fragments 0/" + totalFragments.ToString(CultureInfo.InvariantCulture), 600, 42, 324, 22);
        fragmentLabel.ForeColor = ColorFromRgb(180, 180, 180);
        form.Controls.Add(fragmentLabel);
        FragmentProgress fragmentProgress = new FragmentProgress(fragmentLabel, totalFragments);

        Panel line = new Panel();
        line.Left = 18;
        line.Top = 78;
        line.Width = 906;
        line.Height = 106;
        line.BackColor = ColorFromRgb(24, 24, 24);
        form.Controls.Add(line);

        CreatePipelineLine(line, steps);
        RefreshPipelineLine(steps, statePath);

        Label current = DarkLabel("Preparing render plan...", 18, 196, 906, 22);
        current.ForeColor = ColorFromRgb(210, 210, 210);
        form.Controls.Add(current);

        ProgressBar overall = new ProgressBar();
        overall.Left = 18;
        overall.Top = 224;
        overall.Width = 906;
        overall.Height = 18;
        overall.Minimum = 0;
        overall.Maximum = 100;
        overall.Value = 0;
        form.Controls.Add(overall);

        TextBox log = DarkTextBox(18, 260, 906, 330);
        log.Multiline = true;
        log.ReadOnly = true;
        log.ScrollBars = ScrollBars.Vertical;
        log.WordWrap = false;
        form.Controls.Add(log);

        Button close = DarkButton("Close", 804, 604, 120, 34, false);
        close.Enabled = false;
        close.Click += delegate(object sender, EventArgs e) { form.Close(); };
        form.Controls.Add(close);

        form.Shown += delegate(object sender, EventArgs e)
        {
            bool success = false;
            PipelineElapsed totalWatch = new PipelineElapsed(Path.Combine(outDir, "hybrid-elapsed-ticks.txt"));
            var timerRefresh = new System.Windows.Forms.Timer();
            timerRefresh.Interval = 1000;
            double estimatedTotal = EstimateTotalSeconds(startFrame, endFrame, fps, complex, simple);
            timerRefresh.Tick += delegate { UpdateTimerLabel(timer, totalWatch.Elapsed, estimatedTotal); };
            timerRefresh.Start();
            UpdateTimerLabel(timer, totalWatch.Elapsed, estimatedTotal);
            try
            {
                AppendProgress(log, logPath, "Log: " + logPath);
                if (totalWatch.Previous > TimeSpan.Zero)
                    AppendProgress(log, logPath, "Resuming accumulated elapsed time: " + FormatDuration(totalWatch.Previous));
                AppendProgress(log, logPath, "Final output folder: " + outputDir);
                AppendProgress(log, logPath, "Temporary root folder: " + tempRootDir);
                AppendProgress(log, logPath, "Final MP4: " + finalFile);
                AppendProgress(log, logPath, "Work folder: " + outDir);
                AppendProgress(log, logPath, "Selection: frames " +
                    startFrame.ToString(CultureInfo.InvariantCulture) + "-" +
                    endFrame.ToString(CultureInfo.InvariantCulture) +
                    ", expected duration " + Num(expectedDurationSeconds) +
                    " sec at " + Num(fps) + " fps");
                AppendProgress(log, logPath, "Plan: " +
                    complex.Count.ToString(CultureInfo.InvariantCulture) + " VEGAS range(s), " +
                    simple.Count.ToString(CultureInfo.InvariantCulture) + " FFmpeg simple segment(s), " +
                    BuildOrderedParts(simple, complex, Path.Combine(outDir, "vegas-parts")).Count.ToString(CultureInfo.InvariantCulture) + " video part(s)");
                AppendProgress(log, logPath, "Video template: " + run.VideoTemplateDisplay);
                AppendProgress(log, logPath, "Audio template: " + run.AudioTemplateDisplay);
                AppendProgress(log, logPath, "Start stage: " + PipelineStageName(selectedStartStepIndex));
                if (selectedStartStepIndex >= 0)
                    AppendProgress(log, logPath, "Previous stages will be validated and reused. Selected and later stages will run again.");
                AppendProgress(log, logPath, "Duration tolerance: +/- " + Num(DurationToleranceSeconds(fps)) + " sec");
                VerifyFfprobeAvailable(log, logPath);
                SetFragmentProgress(fragmentProgress, 0);

                RunPipelineStep(steps[0], log, logPath, statePath, timer, totalWatch, estimatedTotal, overall, current, 0, steps.Length, selectedStartStepIndex, delegate()
                {
                    RenderVegasPartsWithTemplate(vegas, complex, outDir, fps, run.VideoTemplate, log, logPath, steps[0], current, fragmentProgress);
                });
                SetFragmentProgress(fragmentProgress, complex.Count);
                RunPipelineStep(steps[1], log, logPath, statePath, timer, totalWatch, estimatedTotal, overall, current, 1, steps.Length, selectedStartStepIndex, delegate()
                {
                    RunPowerShellScript(ffmpegScript, log, logPath, timer, totalWatch, estimatedTotal, current, fragmentProgress, complex.Count);
                });
                SetFragmentProgress(fragmentProgress, totalFragments);
                RunPipelineStep(steps[2], log, logPath, statePath, timer, totalWatch, estimatedTotal, overall, current, 2, steps.Length, selectedStartStepIndex, delegate()
                {
                    RetryInAnotherFolder(form, "Assemble video", videoOnlyFile, log, logPath, delegate(string target)
                    {
                        string temporaryScript = Path.Combine(Path.GetTempPath(), "vegas-hybrid-" + Guid.NewGuid().ToString("N") + ".ps1");
                        try
                        {
                            WriteAssembleScript(temporaryScript, simple, complex, fps, outDir, target);
                            RunPowerShellScript(temporaryScript, log, logPath, timer, totalWatch, estimatedTotal, current);
                        }
                        finally
                        {
                            DeleteRetryMetadata(temporaryScript);
                            DeleteRetryMetadata(Path.ChangeExtension(temporaryScript, ".ffconcat"));
                        }
                        if (!VideoMediaReady(target, expectedDurationSeconds, fps))
                            throw new ApplicationException("The assembled video did not pass validation: " + target);
                        if (!String.Equals(target, videoOnlyFile, StringComparison.OrdinalIgnoreCase))
                        {
                            videoOnlyFile = target;
                            TryWriteText(redirectState, target, false);
                        }
                    });
                    CleanupVideoFragmentFiles(simple, complex, outDir, log, logPath);
                    if (File.Exists(videoOnlyFile) && !String.Equals(videoOnlyFile, Path.Combine(outDir, "hybrid_video_only.mp4"), StringComparison.OrdinalIgnoreCase))
                        TryWriteText(redirectState, videoOnlyFile, false);
                });
                RunPipelineStep(steps[3], log, logPath, statePath, timer, totalWatch, estimatedTotal, overall, current, 3, steps.Length, selectedStartStepIndex, delegate()
                {
                    RenderAudioFromVegasWithTemplate(vegas, startFrame, endFrame, audioFile, run.AudioTemplate, log, logPath, current, fps);
                });
                RunPipelineStep(steps[4], log, logPath, statePath, timer, totalWatch, estimatedTotal, overall, current, 4, steps.Length, selectedStartStepIndex, delegate()
                {
                    RetryInAnotherFolder(form, "Mux final MP4", finalFile, log, logPath, delegate(string target)
                    {
                        string temporaryScript = Path.Combine(Path.GetTempPath(), "vegas-hybrid-" + Guid.NewGuid().ToString("N") + ".ps1");
                        try
                        {
                            WriteMuxScript(temporaryScript, outDir, audioFile, target, expectedDurationSeconds, videoOnlyFile);
                            RunPowerShellScript(temporaryScript, log, logPath, timer, totalWatch, estimatedTotal, current);
                        }
                        finally
                        {
                            DeleteRetryMetadata(temporaryScript);
                        }
                        finalFile = target;
                        finalLogPath = Path.Combine(Path.GetDirectoryName(target), Path.GetFileNameWithoutExtension(target) + ".log.txt");
                    });
                });
                RunPipelineStep(steps[5], log, logPath, statePath, timer, totalWatch, estimatedTotal, overall, current, 5, steps.Length, selectedStartStepIndex, delegate()
                {
                    ValidateFinalDuration(finalFile, expectedDurationSeconds, fps, log, logPath);
                });

                totalWatch.Stop();
                AppendProgress(log, logPath, "Total active render time: " + FormatDuration(totalWatch.Elapsed));
                SetStatusDetail(current, "Render finished. Copying final log and deleting temporary folder.");
                AppendProgress(log, logPath, "Done: " + finalFile);
                AppendProgress(log, logPath, "Copying final log: " + finalLogPath);
                AppendProgress(log, logPath, "Deleting temporary folder after successful render: " + outDir);
                File.Copy(logPath, finalLogPath, true);
                try
                {
                    SafeDeleteRunDirectory(outDir, tempRootDir);
                    File.AppendAllText(finalLogPath,
                        "[" + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "] Temporary folder deleted: " + outDir + "\r\n",
                        Encoding.UTF8);
                }
                catch (Exception cleanupEx)
                {
                    string cleanupLine = "[" + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "] Could not delete temporary folder: " + cleanupEx.Message + "\r\n";
                    try
                    {
                        File.AppendAllText(finalLogPath, cleanupLine, Encoding.UTF8);
                    }
                    catch
                    {
                    }
                    try
                    {
                        log.AppendText(cleanupLine);
                        log.SelectionStart = log.TextLength;
                        log.ScrollToCaret();
                    }
                    catch
                    {
                    }
                }
                UpdateTimerLabel(timer, totalWatch.Elapsed, estimatedTotal);
                SetOverallProgress(overall, 100);
                success = true;
            }
            catch (Exception ex)
            {
                totalWatch.Stop();
                AppendProgress(log, logPath, "ERROR: " + ex.Message);
                SetStatusDetail(current, "Failed. Temporary folder was kept: " + outDir);
                RefreshPipelineLine(steps, statePath);
                MessageBox.Show(ex.Message, "Hybrid render", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                timerRefresh.Dispose();
                totalWatch.Stop();
                close.Enabled = true;
                if (success)
                {
                    TimeSpan completedElapsed = totalWatch.Elapsed;
                    SetStatusDetail(current, "Done. Final MP4 created: " + finalFile);
                    form.BeginInvoke(new MethodInvoker(delegate()
                    {
                        if (!form.IsDisposed)
                        {
                            MessageBox.Show(form, "Hybrid render completed in " + FormatDuration(completedElapsed) + ".\r\n\r\n" + finalFile,
                                "Hybrid render", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            OpenFinalOutputFolder(finalFile);
                        }
                    }));
                }
            }
        };

        form.ShowDialog();
    }

    private sealed class PipelineElapsed
    {
        private readonly string path;
        private readonly object gate = new object();
        private readonly Stopwatch watch;
        private readonly System.Threading.Timer checkpoint;
        private bool stopped;

        public PipelineElapsed(string path)
        {
            this.path = path;
            long ticks;
            try
            {
                if (File.Exists(path) && Int64.TryParse(File.ReadAllText(path).Trim(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out ticks) && ticks >= 0)
                    Previous = TimeSpan.FromTicks(ticks);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            watch = Stopwatch.StartNew();
            checkpoint = new System.Threading.Timer(SaveCheckpoint, null, 5000, 5000);
        }

        public TimeSpan Previous { get; private set; }
        public TimeSpan Elapsed
        {
            get { lock (gate) return Previous + watch.Elapsed; }
        }

        private void SaveCheckpoint(object unused)
        {
            lock (gate)
            {
                if (!stopped) SaveLocked();
            }
        }

        private void SaveLocked()
        {
            string pending = path + ".tmp";
            try
            {
                File.WriteAllText(pending, Elapsed.Ticks.ToString(CultureInfo.InvariantCulture));
                if (File.Exists(path)) File.Replace(pending, path, null);
                else File.Move(pending, path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public void Stop()
        {
            lock (gate)
            {
                if (stopped) return;
                watch.Stop();
                stopped = true;
                checkpoint.Dispose();
                SaveLocked();
            }
        }
    }

    private void RetryInAnotherFolder(Form owner, string stage, string target, TextBox log, string logPath, Action<string> action)
    {
        string originalName = Path.GetFileNameWithoutExtension(target);
        while (true)
        {
            try { action(target); return; }
            catch (Exception ex)
            {
                AppendProgress(log, logPath, stage + " failed at " + target + ": " + ex.Message);
                DialogResult choice = MessageBox.Show(owner, stage + " failed.\r\n\r\n" + ex.Message +
                    "\r\n\r\nChoose another folder and retry this stage? Completed inputs will be reused.",
                    "Hybrid render", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning);
                if (choice != DialogResult.Retry) throw;
                string folder = ChooseWorkDirectory(Path.GetDirectoryName(target));
                if (String.IsNullOrEmpty(folder)) throw;
                target = Path.Combine(folder, originalName + ".mp4");
                for (int i = 2; File.Exists(target); i++)
                    target = Path.Combine(folder, originalName + "-" + i.ToString(CultureInfo.InvariantCulture) + ".mp4");
                AppendProgress(log, logPath, "Retrying " + stage + " to: " + target);
            }
        }
    }

    private void DeleteRetryMetadata(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void TryWriteText(string path, string value, bool append)
    {
        try
        {
            if (append) File.AppendAllText(path, value, Encoding.UTF8);
            else File.WriteAllText(path, value, Encoding.UTF8);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void AppendProgress(TextBox log, string logPath, string line)
    {
        string text = "[" + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "] " + line;
        TryWriteText(logPath, text + "\r\n", true);
        log.AppendText(text + "\r\n");
        log.SelectionStart = log.TextLength;
        log.ScrollToCaret();
        Application.DoEvents();
    }

    private void AppendProcessLine(TextBox log, Label current, string line)
    {
        if (String.IsNullOrEmpty(line) || log == null || log.IsDisposed)
            return;

        MethodInvoker update = delegate()
        {
            if (log.IsDisposed)
                return;
            log.AppendText(line + "\r\n");
            log.SelectionStart = log.TextLength;
            log.ScrollToCaret();
            SetStatusDetail(current, ShortStatusLine(line));
        };

        try
        {
            if (log.InvokeRequired)
                log.BeginInvoke(update);
            else
                update();
        }
        catch
        {
        }
    }

    private string ShortStatusLine(string line)
    {
        string text = line.Trim();
        if (text.Length > 138)
            return text.Substring(0, 138) + "...";
        return text;
    }

    private void SetStatusDetail(Label label, string text)
    {
        if (label == null || label.IsDisposed)
            return;
        label.Text = ShortStatusLine(text);
        Application.DoEvents();
    }

    private void SetOverallProgress(ProgressBar bar, int value)
    {
        if (bar == null || bar.IsDisposed)
            return;
        if (value < bar.Minimum)
            value = bar.Minimum;
        if (value > bar.Maximum)
            value = bar.Maximum;
        bar.Value = value;
        Application.DoEvents();
    }

    private void SetStepProgress(PipelineStep step, int done, int total)
    {
        if (step == null || step.StatusLabel == null || step.StatusLabel.IsDisposed)
            return;
        if (total <= 0)
            step.StatusLabel.Text = "Running";
        else
            step.StatusLabel.Text = done.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture);
        Application.DoEvents();
    }

    private PipelineStep[] BuildPipelineSteps(string outDir, List<Range> complex, List<SimpleSegment> simple,
        Func<string> videoOnlyFile, string audioFile, Func<string> finalFile, double fps, double expectedDurationSeconds)
    {
        return new PipelineStep[]
        {
            new PipelineStep("VEGAS", "Complex ranges", delegate() { return VideoMediaReady(videoOnlyFile(), expectedDurationSeconds, fps) || VegasPartsComplete(outDir, complex, fps); }),
            new PipelineStep("FFmpeg", "Simple ranges", delegate() { return VideoMediaReady(videoOnlyFile(), expectedDurationSeconds, fps) || SimplePartsComplete(simple, fps); }),
            new PipelineStep("Video", "Concat copy", delegate() { return VideoMediaReady(videoOnlyFile(), expectedDurationSeconds, fps); }),
            new PipelineStep("Audio", "WAV stem", delegate() { return MediaDurationReady(audioFile, expectedDurationSeconds, fps); }),
            new PipelineStep("Final", "MP4 mux", delegate() { return MediaDurationReady(finalFile(), expectedDurationSeconds, fps); }),
            new PipelineStep("Check", "Duration", delegate() { return false; }, true)
        };
    }

    private void CreatePipelineLine(Panel parent, PipelineStep[] steps)
    {
        int left = 14;
        int gap = 12;
        int boxWidth = (parent.Width - 28 - (steps.Length - 1) * gap) / steps.Length;
        for (int i = 0; i < steps.Length; i++)
        {
            Panel box = new Panel();
            box.Left = left;
            box.Top = 18;
            box.Width = boxWidth;
            box.Height = 68;
            box.BackColor = ColorFromRgb(64, 64, 64);
            box.BorderStyle = BorderStyle.FixedSingle;
            parent.Controls.Add(box);

            Label title = DarkLabel(steps[i].Name, 10, 8, boxWidth - 20, 22);
            title.Font = new Font("Segoe UI", 9.0f, FontStyle.Bold);
            box.Controls.Add(title);

            Label detail = DarkLabel(steps[i].Detail, 10, 32, boxWidth - 20, 18);
            detail.ForeColor = ColorFromRgb(210, 210, 210);
            box.Controls.Add(detail);

            Label status = DarkLabel("Waiting", 10, 48, boxWidth - 20, 18);
            status.ForeColor = ColorFromRgb(210, 210, 210);
            box.Controls.Add(status);

            steps[i].Box = box;
            steps[i].StatusLabel = status;

            left += boxWidth + gap;
        }
    }

    private void RefreshPipelineLine(PipelineStep[] steps, string statePath)
    {
        StringBuilder state = new StringBuilder();
        for (int i = 0; i < steps.Length; i++)
        {
            if (steps[i].Status != StepStatus.Running && steps[i].Status != StepStatus.Failed)
                steps[i].Status = (!steps[i].AlwaysRun && steps[i].IsComplete()) ? StepStatus.Done : StepStatus.Pending;
            ApplyStepStatus(steps[i]);
            state.AppendLine(steps[i].Name + "=" + steps[i].Status.ToString());
        }
        File.WriteAllText(statePath, state.ToString(), Encoding.UTF8);
        Application.DoEvents();
    }

    private void ApplyStepStatus(PipelineStep step)
    {
        if (step.Box == null || step.StatusLabel == null)
            return;

        if (step.Status == StepStatus.Done)
        {
            step.Box.BackColor = ColorFromRgb(34, 108, 68);
            step.StatusLabel.Text = "Done";
        }
        else if (step.Status == StepStatus.Running)
        {
            step.Box.BackColor = ColorFromRgb(165, 122, 26);
            step.StatusLabel.Text = "Running";
        }
        else if (step.Status == StepStatus.Failed)
        {
            step.Box.BackColor = ColorFromRgb(142, 48, 52);
            step.StatusLabel.Text = "Failed";
        }
        else
        {
            step.Box.BackColor = ColorFromRgb(64, 64, 64);
            step.StatusLabel.Text = "Waiting";
        }
    }

    private string PipelineStageName(int stepIndex)
    {
        if (stepIndex == 0)
            return "1. VEGAS complex ranges";
        if (stepIndex == 1)
            return "2. FFmpeg simple ranges";
        if (stepIndex == 2)
            return "3. Assemble video";
        if (stepIndex == 3)
            return "4. Render audio WAV";
        if (stepIndex == 4)
            return "5. Mux final MP4";
        if (stepIndex == 5)
            return "6. Validate final MP4";
        return "Auto / continue from detected outputs";
    }

    private void RunPipelineStep(PipelineStep step, TextBox log, string logPath, string statePath, Label timer,
        PipelineElapsed totalWatch, double estimatedTotal, ProgressBar overall, Label current, int stepIndex, int stepCount, int selectedStartStepIndex, StepAction action)
    {
        if (selectedStartStepIndex >= 0 && stepIndex < selectedStartStepIndex)
        {
            if (!step.AlwaysRun && step.IsComplete())
            {
                step.Status = StepStatus.Done;
                ApplyStepStatus(step);
                SetOverallProgress(overall, ProgressAfterStep(stepIndex, stepCount));
                SetStatusDetail(current, "Reuse " + step.Name + ": existing output passed checks.");
                AppendProgress(log, logPath, "Reuse " + step.Name + ": existing output passed checks.");
                WriteSingleStepState(statePath, step);
                return;
            }

            step.Status = StepStatus.Failed;
            ApplyStepStatus(step);
            WriteSingleStepState(statePath, step);
            throw new ApplicationException("Cannot start at " + PipelineStageName(selectedStartStepIndex) +
                " because a previous stage is not ready: " + step.Name + " (" + step.Detail + ").");
        }

        bool forceRun = selectedStartStepIndex >= 0 && stepIndex >= selectedStartStepIndex && !step.AlwaysRun;
        if (!forceRun && !step.AlwaysRun && step.IsComplete())
        {
            step.Status = StepStatus.Done;
            ApplyStepStatus(step);
            SetOverallProgress(overall, ProgressAfterStep(stepIndex, stepCount));
            SetStatusDetail(current, "Skip " + step.Name + ": existing output already passed checks.");
            AppendProgress(log, logPath, "Skip " + step.Name + ": existing output already passed checks.");
            WriteSingleStepState(statePath, step);
            return;
        }

        step.Status = StepStatus.Running;
        ApplyStepStatus(step);
        WriteSingleStepState(statePath, step);
        SetOverallProgress(overall, ProgressBeforeStep(stepIndex, stepCount));
        SetStatusDetail(current, "Step " + (stepIndex + 1).ToString(CultureInfo.InvariantCulture) + "/" +
            stepCount.ToString(CultureInfo.InvariantCulture) + " - " + step.Name + ": " + step.Detail);
        if (forceRun)
            AppendProgress(log, logPath, "Start " + step.Name + ": " + step.Detail + " (selected restart)");
        else
            AppendProgress(log, logPath, "Start " + step.Name + ": " + step.Detail);
        UpdateTimerLabel(timer, totalWatch.Elapsed, estimatedTotal);

        Stopwatch stepWatch = Stopwatch.StartNew();
        try
        {
            action();
            stepWatch.Stop();
            if (!step.AlwaysRun && !step.IsComplete())
                throw new ApplicationException("Step finished but expected output was not found: " + step.Name);
            step.Status = StepStatus.Done;
            ApplyStepStatus(step);
            WriteSingleStepState(statePath, step);
            SetOverallProgress(overall, ProgressAfterStep(stepIndex, stepCount));
            SetStatusDetail(current, "Done " + step.Name + " in " + FormatDuration(stepWatch.Elapsed));
            AppendProgress(log, logPath, "Done " + step.Name + " in " + FormatDuration(stepWatch.Elapsed));
            UpdateTimerLabel(timer, totalWatch.Elapsed, estimatedTotal);
        }
        catch
        {
            stepWatch.Stop();
            step.Status = StepStatus.Failed;
            ApplyStepStatus(step);
            WriteSingleStepState(statePath, step);
            SetStatusDetail(current, "Failed " + step.Name + " after " + FormatDuration(stepWatch.Elapsed));
            AppendProgress(log, logPath, "Failed " + step.Name + " after " + FormatDuration(stepWatch.Elapsed));
            throw;
        }
    }

    private int ProgressBeforeStep(int stepIndex, int stepCount)
    {
        if (stepCount <= 0)
            return 0;
        return (int)Math.Round((100.0 * stepIndex) / stepCount);
    }

    private int ProgressAfterStep(int stepIndex, int stepCount)
    {
        if (stepCount <= 0)
            return 100;
        return (int)Math.Round((100.0 * (stepIndex + 1)) / stepCount);
    }

    private void WriteSingleStepState(string statePath, PipelineStep step)
    {
        TryWriteText(statePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
            " " + step.Name + "=" + step.Status.ToString() + "\r\n", true);
    }

    private bool VegasPartsComplete(string outDir, List<Range> ranges, double fps)
    {
        if (ranges.Count == 0)
            return true;
        string vegasDir = Path.Combine(outDir, "vegas-parts");
        for (int i = 0; i < ranges.Count; i++)
        {
            Range range = ranges[i];
            string file = Path.Combine(vegasDir, String.Format(CultureInfo.InvariantCulture,
                "vegas_{0:000}_{1}_{2}.mp4", i, range.Start, range.End));
            if (!VideoMediaReady(file, ((double)(range.End - range.Start)) / fps, fps))
                return false;
        }
        return true;
    }

    private bool SimplePartsComplete(List<SimpleSegment> simple, double fps)
    {
        for (int i = 0; i < simple.Count; i++)
        {
            double expectedSeconds = ((double)(simple[i].TimelineEnd - simple[i].TimelineStart)) / fps;
            if (!VideoMediaReady(simple[i].OutFile, expectedSeconds, fps))
                return false;
        }
        return true;
    }

    private void CleanupVideoFragmentFiles(List<SimpleSegment> simple, List<Range> complex, string outDir, TextBox log, string logPath)
    {
        List<string> files = new List<string>();
        for (int i = 0; i < simple.Count; i++)
            files.Add(simple[i].OutFile);

        string vegasDir = Path.Combine(outDir, "vegas-parts");
        for (int i = 0; i < complex.Count; i++)
        {
            Range range = complex[i];
            files.Add(Path.Combine(vegasDir, String.Format(CultureInfo.InvariantCulture,
                "vegas_{0:000}_{1}_{2}.mp4", i, range.Start, range.End)));
        }

        if (files.Count == 0)
            return;

        AppendProgress(log, logPath, "Deleting video fragment files after successful concat.");
        Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        int deleted = 0;
        int missing = 0;
        int failed = 0;
        long freedBytes = 0;

        foreach (string file in files)
        {
            if (seen.ContainsKey(file))
                continue;
            seen[file] = true;

            try
            {
                if (!File.Exists(file))
                {
                    missing++;
                    continue;
                }

                FileInfo info = new FileInfo(file);
                long bytes = info.Length;
                File.Delete(file);
                deleted++;
                freedBytes += bytes;
            }
            catch (Exception ex)
            {
                failed++;
                AppendProgress(log, logPath, "  Could not delete fragment: " + file + " (" + ex.Message + ")");
            }
        }

        AppendProgress(log, logPath, "Fragment cleanup: deleted " +
            deleted.ToString(CultureInfo.InvariantCulture) + " file(s), freed " +
            freedBytes.ToString("n0", CultureInfo.InvariantCulture) + " bytes; missing " +
            missing.ToString(CultureInfo.InvariantCulture) + ", failed " +
            failed.ToString(CultureInfo.InvariantCulture) + ".");
    }

    private bool FileReady(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private bool MediaDurationReady(string path, double expectedSeconds, double fps)
    {
        if (!FileReady(path))
            return false;

        double actualSeconds;
        if (!TryProbeDurationSeconds(path, out actualSeconds))
            return false;

        return Math.Abs(actualSeconds - expectedSeconds) <= DurationToleranceSeconds(fps);
    }

    private bool VideoMediaReady(string path, double expectedSeconds, double fps)
    {
        return MediaDurationReady(path, expectedSeconds, fps) && HasVideoStream(path);
    }

    private bool TryProbeDurationSeconds(string path, out double durationSeconds)
    {
        durationSeconds = 0.0;
        try
        {
            durationSeconds = ProbeDurationSeconds(path);
            return durationSeconds > 0.0;
        }
        catch
        {
            return false;
        }
    }

    private bool HasVideoStream(string path)
    {
        if (!File.Exists(path))
            return false;

        ProcessStartInfo info = new ProcessStartInfo();
        info.FileName = "ffprobe";
        info.Arguments = "-v error -select_streams v:0 -show_entries stream=codec_type -of default=noprint_wrappers=1:nokey=1 " + CmdQuote(path);
        info.UseShellExecute = false;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.CreateNoWindow = true;

        using (Process process = new Process())
        {
            process.StartInfo = info;
            try
            {
                process.Start();
            }
            catch
            {
                return false;
            }

            string output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
                return false;

            string[] lines = output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                if (String.Equals(lines[i].Trim(), "video", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private void RunPowerShellScript(string scriptPath, TextBox log, string logPath, Label timer, PipelineElapsed totalWatch, double estimatedTotal, Label current)
    {
        RunPowerShellScript(scriptPath, log, logPath, timer, totalWatch, estimatedTotal, current, null, 0);
    }

    private void RunPowerShellScript(string scriptPath, TextBox log, string logPath, Label timer, PipelineElapsed totalWatch, double estimatedTotal, Label current,
        FragmentProgress fragmentProgress, int completedBeforeScript)
    {
        if (!File.Exists(scriptPath))
            throw new ApplicationException("Script does not exist:\r\n" + scriptPath);

        AppendProgress(log, logPath, "PowerShell: " + scriptPath);

        ProcessStartInfo info = new ProcessStartInfo();
        info.FileName = "powershell.exe";
        info.Arguments = "-NoProfile -ExecutionPolicy Bypass -File " + CmdQuote(scriptPath);
        info.WorkingDirectory = Path.GetDirectoryName(scriptPath);
        info.UseShellExecute = false;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;
        info.CreateNoWindow = true;

        using (Process process = new Process())
        {
            process.StartInfo = info;
            object logLock = new object();
            DataReceivedEventHandler writeOutput = delegate(object sender, DataReceivedEventArgs e)
            {
                if (e.Data == null)
                    return;
                lock (logLock)
                {
                    TryWriteText(logPath, e.Data + "\r\n", true);
                }
                AppendProcessLine(log, current, e.Data);
                UpdateFragmentProgressFromProcessLine(fragmentProgress, completedBeforeScript, e.Data);
            };
            process.OutputDataReceived += writeOutput;
            process.ErrorDataReceived += writeOutput;
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            while (!process.HasExited)
            {
                UpdateTimerLabel(timer, totalWatch.Elapsed, estimatedTotal);
                Application.DoEvents();
                System.Threading.Thread.Sleep(500);
            }
            process.WaitForExit();

            if (process.ExitCode != 0)
                throw new ApplicationException("PowerShell script failed with exit code " +
                    process.ExitCode.ToString(CultureInfo.InvariantCulture) + ":\r\n" + scriptPath);
        }
    }

    private void UpdateFragmentProgressFromProcessLine(FragmentProgress fragmentProgress, int completedBeforeScript, string line)
    {
        if (fragmentProgress == null || String.IsNullOrEmpty(line))
            return;

        string prefix = "FFmpeg simple done ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
            return;

        int start = prefix.Length;
        int slash = line.IndexOf('/', start);
        if (slash <= start)
            return;

        int done;
        if (Int32.TryParse(line.Substring(start, slash - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out done))
            SetFragmentProgress(fragmentProgress, completedBeforeScript + done);
    }

    private void SetFragmentProgress(FragmentProgress fragmentProgress, int done)
    {
        if (fragmentProgress == null || fragmentProgress.Label == null || fragmentProgress.Label.IsDisposed)
            return;

        if (done < 0)
            done = 0;
        if (done > fragmentProgress.Total)
            done = fragmentProgress.Total;
        fragmentProgress.Done = done;

        if (fragmentProgress.Label.InvokeRequired)
        {
            fragmentProgress.Label.BeginInvoke(new MethodInvoker(delegate()
            {
                ApplyFragmentProgress(fragmentProgress);
            }));
            return;
        }

        ApplyFragmentProgress(fragmentProgress);
    }

    private void ApplyFragmentProgress(FragmentProgress fragmentProgress)
    {
        if (fragmentProgress == null || fragmentProgress.Label == null || fragmentProgress.Label.IsDisposed)
            return;

        fragmentProgress.Label.Text = "Fragments " +
            fragmentProgress.Done.ToString(CultureInfo.InvariantCulture) + "/" +
            fragmentProgress.Total.ToString(CultureInfo.InvariantCulture);
        Application.DoEvents();
    }

    private void OpenFinalOutputFolder(string finalFile)
    {
        try
        {
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = "explorer.exe";
            if (File.Exists(finalFile))
                info.Arguments = "/select," + CmdQuote(finalFile);
            else
                info.Arguments = CmdQuote(Path.GetDirectoryName(finalFile));
            info.UseShellExecute = true;
            Process.Start(info);
        }
        catch
        {
        }
    }

    private void UpdateTimerLabel(Label timer, TimeSpan elapsed, double estimatedTotalSeconds)
    {
        string expected = estimatedTotalSeconds > 0 ? FormatDuration(TimeSpan.FromSeconds(estimatedTotalSeconds)) : "--:--";
        string remaining = "--:--";
        if (estimatedTotalSeconds > 0)
        {
            double left = estimatedTotalSeconds - elapsed.TotalSeconds;
            remaining = left > 0 ? FormatDuration(TimeSpan.FromSeconds(left)) : "estimating";
        }
        timer.Text = "Elapsed " + FormatDuration(elapsed) + " | Expected " + expected + " | Remaining " + remaining;
        Application.DoEvents();
    }

    private double EstimateTotalSeconds(long startFrame, long endFrame, double fps, List<Range> complex, List<SimpleSegment> simple)
    {
        double totalSeconds = ((double)(endFrame - startFrame)) / fps;
        double complexSeconds = 0.0;
        for (int i = 0; i < complex.Count; i++)
            complexSeconds += ((double)(complex[i].End - complex[i].Start)) / fps;
        double simpleSeconds = 0.0;
        for (int i = 0; i < simple.Count; i++)
            simpleSeconds += ((double)(simple[i].TimelineEnd - simple[i].TimelineStart)) / fps;

        double vegasEstimate = Math.Max(5.0, complexSeconds * 1.4);
        double ffmpegEstimate = Math.Max(5.0, simpleSeconds / 3.0);
        double concatEstimate = Math.Max(5.0, totalSeconds / 12.0);
        double audioEstimate = Math.Max(5.0, totalSeconds / 8.0);
        double muxEstimate = Math.Max(5.0, totalSeconds / 12.0);
        double checkEstimate = 3.0;
        return vegasEstimate + ffmpegEstimate + concatEstimate + audioEstimate + muxEstimate + checkEstimate;
    }

    private string FormatDuration(TimeSpan value)
    {
        if (value.TotalHours >= 1.0)
            return ((long)value.TotalHours).ToString(CultureInfo.InvariantCulture) + value.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture);
        return value.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    private Label DarkLabel(string text, int left, int top, int width, int height)
    {
        Label label = new Label();
        label.Text = text;
        label.Left = left;
        label.Top = top;
        label.Width = width;
        label.Height = height;
        label.ForeColor = ColorFromRgb(235, 235, 235);
        label.BackColor = Color.Transparent;
        label.Font = new Font("Segoe UI", 9.0f, FontStyle.Regular);
        return label;
    }

    private TextBox DarkTextBox(int left, int top, int width, int height)
    {
        TextBox text = new TextBox();
        text.Left = left;
        text.Top = top;
        text.Width = width;
        text.Height = height;
        text.BorderStyle = BorderStyle.FixedSingle;
        text.BackColor = ColorFromRgb(24, 24, 24);
        text.ForeColor = ColorFromRgb(235, 235, 235);
        text.Font = new Font("Consolas", 9.0f, FontStyle.Regular);
        return text;
    }

    private ComboBox DarkCombo(int left, int top, int width, int height)
    {
        ComboBox combo = new ComboBox();
        combo.Left = left;
        combo.Top = top;
        combo.Width = width;
        combo.Height = height;
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.BackColor = ColorFromRgb(24, 24, 24);
        combo.ForeColor = ColorFromRgb(235, 235, 235);
        combo.FlatStyle = FlatStyle.Flat;
        combo.Font = new Font("Segoe UI", 9.0f, FontStyle.Regular);
        return combo;
    }

    private Button DarkButton(string text, int left, int top, int width, int height, bool accent)
    {
        Button button = new Button();
        button.Text = text;
        button.Left = left;
        button.Top = top;
        button.Width = width;
        button.Height = height;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = accent ? ColorFromRgb(77, 134, 255) : ColorFromRgb(82, 82, 82);
        button.FlatAppearance.BorderSize = 1;
        button.BackColor = accent ? ColorFromRgb(47, 95, 190) : ColorFromRgb(45, 45, 48);
        button.ForeColor = ColorFromRgb(245, 245, 245);
        button.Font = new Font("Segoe UI", accent ? 10.0f : 9.0f, accent ? FontStyle.Bold : FontStyle.Regular);
        return button;
    }

    private Color ColorFromRgb(int red, int green, int blue)
    {
        return Color.FromArgb(red, green, blue);
    }

    private List<RenderChoice> BuildVideoRenderChoices(Vegas vegas)
    {
        List<RenderChoice> choices = new List<RenderChoice>();
        foreach (Renderer renderer in vegas.Renderers)
        {
            if (renderer == null || !renderer.IsActive)
                continue;
            if (!RendererSupportsVideo(renderer))
                continue;
            foreach (RenderTemplate template in renderer.Templates)
            {
                RenderChoice choice = new RenderChoice(renderer, template);
                if (template != null && template.IsValid())
                    choices.Add(choice);
            }
        }
        return choices;
    }

    private bool RendererSupportsVideo(Renderer renderer)
    {
        try
        {
            object value = renderer.GetType().GetProperty("SupportsVideo").GetValue(renderer, null);
            if (value is bool)
                return (bool)value;
        }
        catch
        {
        }

        string extension = renderer.FileExtension == null ? "" : renderer.FileExtension.ToLowerInvariant();
        string text = (renderer.Name + " " + renderer.FileTypeName + " " + extension).ToLowerInvariant();
        if (extension == "wav" || extension == "w64" || extension == "mp3" || extension == "aac" || extension == "flac" || extension == "ogg")
            return false;
        return text.IndexOf("video") >= 0 ||
            text.IndexOf("avc") >= 0 ||
            text.IndexOf("hevc") >= 0 ||
            text.IndexOf("xavc") >= 0 ||
            extension == "mp4" ||
            extension == "mov" ||
            extension == "mxf" ||
            extension == "avi";
    }

    private List<RenderChoice> BuildAudioRenderChoices(Vegas vegas)
    {
        List<RenderChoice> choices = new List<RenderChoice>();
        foreach (Renderer renderer in vegas.Renderers)
        {
            if (renderer == null || !renderer.IsActive)
                continue;

            bool audioRenderer = false;
            try
            {
                audioRenderer = renderer.SupportsAudio;
            }
            catch
            {
                audioRenderer = false;
            }

            string rendererText = (renderer.Name + " " + renderer.FileTypeName + " " + renderer.FileExtension).ToLowerInvariant();
            if (!audioRenderer && rendererText.IndexOf("wav") < 0 && rendererText.IndexOf("wave") < 0)
                continue;

            foreach (RenderTemplate template in renderer.Templates)
            {
                if (template != null && template.IsValid())
                    choices.Add(new RenderChoice(renderer, template));
            }
        }
        return choices;
    }

    private void SelectPreferredVideoTemplate(ComboBox combo, Vegas vegas)
    {
        if (combo.Items.Count == 0)
            return;

        int bestIndex = 0;
        int bestScore = Int32.MinValue;
        string projectSize = vegas.Project.Video.Width.ToString(CultureInfo.InvariantCulture) + "x" +
            vegas.Project.Video.Height.ToString(CultureInfo.InvariantCulture);

        for (int i = 0; i < combo.Items.Count; i++)
        {
            RenderChoice choice = (RenderChoice)combo.Items[i];
            string text = choice.ToString().ToLowerInvariant();
            int score = 0;
            if (text.IndexOf("magix avc") >= 0)
                score += 100;
            if (text.IndexOf("nv") >= 0 || text.IndexOf("nvenc") >= 0)
                score += 90;
            if (text.IndexOf("mp4") >= 0)
                score += 40;
            if (text.IndexOf(projectSize.ToLowerInvariant()) >= 0)
                score += 30;
            if (text.IndexOf("60") >= 0)
                score += 20;
            if (text.IndexOf("mainconcept") >= 0)
                score -= 70;
            if (text.IndexOf("hevc") >= 0)
                score -= 10;

            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }
        combo.SelectedIndex = bestIndex;
    }

    private void SelectPreferredAudioTemplate(ComboBox combo)
    {
        if (combo.Items.Count == 0)
            return;

        int bestIndex = 0;
        int bestScore = Int32.MinValue;
        for (int i = 0; i < combo.Items.Count; i++)
        {
            RenderChoice choice = (RenderChoice)combo.Items[i];
            string text = choice.ToString().ToLowerInvariant();
            int score = 0;
            if (text.IndexOf("wave (microsoft)") >= 0)
                score += 100;
            if (text.IndexOf("48 000") >= 0 || text.IndexOf("48000") >= 0 || text.IndexOf("48,000") >= 0)
                score += 50;
            if (text.IndexOf("16") >= 0)
                score += 25;
            if (text.IndexOf("stereo") >= 0)
                score += 20;
            if (text.IndexOf("pcm") >= 0)
                score += 20;
            if (text.IndexOf("float") >= 0 || text.IndexOf("5.1") >= 0 || text.IndexOf("mono") >= 0)
                score -= 100;
            if (text.IndexOf("sony wave64") >= 0 || text.IndexOf("scott studios") >= 0)
                score -= 80;

            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }
        combo.SelectedIndex = bestIndex;
    }

    private void RefreshVideoCombo(ComboBox combo, List<RenderChoice> choices, string filter, string preferredKey, Vegas vegas)
    {
        combo.Items.Clear();
        foreach (RenderChoice choice in choices)
        {
            if (RenderChoiceMatches(choice, filter))
                combo.Items.Add(choice);
        }

        if (combo.Items.Count == 0)
            return;
        if (!SelectRenderChoiceByKey(combo, preferredKey))
            SelectPreferredVideoTemplate(combo, vegas);
    }

    private void RefreshAudioCombo(ComboBox combo, List<RenderChoice> choices, string filter, string preferredKey)
    {
        combo.Items.Clear();
        foreach (RenderChoice choice in choices)
        {
            if (RenderChoiceMatches(choice, filter))
                combo.Items.Add(choice);
        }

        if (combo.Items.Count == 0)
            return;
        if (!SelectRenderChoiceByKey(combo, preferredKey))
            SelectPreferredAudioTemplate(combo);
    }

    private bool RenderChoiceMatches(RenderChoice choice, string filter)
    {
        if (String.IsNullOrWhiteSpace(filter))
            return true;

        string haystack = choice.ToString();
        string[] words = filter.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            if (haystack.IndexOf(words[i], StringComparison.OrdinalIgnoreCase) < 0)
                return false;
        }
        return true;
    }

    private bool SelectRenderChoiceByKey(ComboBox combo, string key)
    {
        if (String.IsNullOrEmpty(key))
            return false;

        for (int i = 0; i < combo.Items.Count; i++)
        {
            RenderChoice choice = (RenderChoice)combo.Items[i];
            if (String.Equals(choice.PersistentKey(), key, StringComparison.Ordinal) ||
                String.Equals(choice.ToString(), key, StringComparison.Ordinal))
            {
                combo.SelectedIndex = i;
                return true;
            }
        }
        return false;
    }

    private string GetSharedSettingsPath()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VegasFfmpegHybridRender", "last-used.ini");
    }

    private string GetUserSettingsPath(string projectBase)
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VegasFfmpegHybridRender");
        return Path.Combine(root, MakeSafeFileName(projectBase) + "-settings.txt");
    }

    private string MakeSafeFileName(string value)
    {
        if (String.IsNullOrEmpty(value))
            return "project";

        char[] invalid = Path.GetInvalidFileNameChars();
        StringBuilder result = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char ch = value[i];
            bool isInvalid = false;
            for (int j = 0; j < invalid.Length; j++)
            {
                if (ch == invalid[j])
                {
                    isInvalid = true;
                    break;
                }
            }
            result.Append(isInvalid ? '_' : ch);
        }
        return result.Length == 0 ? "project" : result.ToString();
    }

    private string LoadLastOutputDirectory(string projectSettingsPath, string userSettingsPath, string sharedSettingsPath, string fallback)
    {
        string saved = LoadSettingValue(projectSettingsPath, userSettingsPath, sharedSettingsPath, "OutputDirectory");
        if (!String.IsNullOrEmpty(saved))
            return saved;
        return Directory.Exists(fallback) ? fallback : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private string LoadLastTempDirectory(string projectSettingsPath, string userSettingsPath, string sharedSettingsPath, string fallback)
    {
        string saved = LoadSettingValue(projectSettingsPath, userSettingsPath, sharedSettingsPath, "TempDirectory");
        if (!String.IsNullOrEmpty(saved))
            return saved;

        saved = LoadSettingValue(projectSettingsPath, userSettingsPath, sharedSettingsPath, "OutputDirectory");
        if (!String.IsNullOrEmpty(saved))
            return saved;

        return Directory.Exists(fallback) ? fallback : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private string LoadSettingValue(string projectSettingsPath, string userSettingsPath, string sharedSettingsPath, string key)
    {
        string value = LoadSettingValue(projectSettingsPath, key);
        if (!String.IsNullOrEmpty(value))
            return value;
        value = LoadSettingValue(userSettingsPath, key);
        if (!String.IsNullOrEmpty(value))
            return value;
        return LoadSettingValue(sharedSettingsPath, key);
    }

    private string LoadSettingValue(string settingsPath, string key)
    {
        try
        {
            if (String.IsNullOrEmpty(settingsPath) || !File.Exists(settingsPath))
                return "";

            string prefix = key + "=";
            foreach (string raw in File.ReadAllLines(settingsPath, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(prefix.Length).Trim();
            }
        }
        catch
        {
        }
        return "";
    }

    private void SaveSettings(string projectSettingsPath, string userSettingsPath, string sharedSettingsPath, string tempDirectory, string outputDirectory, string videoTemplateKey, string audioTemplateKey)
    {
        SaveSettingsFile(projectSettingsPath, tempDirectory, outputDirectory, videoTemplateKey, audioTemplateKey);
        SaveSettingsFile(userSettingsPath, tempDirectory, outputDirectory, videoTemplateKey, audioTemplateKey);
        SaveSettingsFile(sharedSettingsPath, tempDirectory, outputDirectory, videoTemplateKey, audioTemplateKey);
    }

    private void SaveSettingsFile(string settingsPath, string tempDirectory, string outputDirectory, string videoTemplateKey, string audioTemplateKey)
    {
        try
        {
            string dir = Path.GetDirectoryName(settingsPath);
            if (!String.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using (StreamWriter writer = new StreamWriter(settingsPath, false, Encoding.UTF8))
            {
                writer.WriteLine("TempDirectory=" + tempDirectory);
                writer.WriteLine("OutputDirectory=" + outputDirectory);
                writer.WriteLine("VideoTemplate=" + videoTemplateKey);
                writer.WriteLine("AudioTemplate=" + audioTemplateKey);
            }
        }
        catch
        {
        }
    }

    private string MakeFinalPath(string rootDir, string projectBase, long startFrame, long endFrame, bool resumeRun)
    {
        string baseName = projectBase;
        string candidate = Path.Combine(rootDir, baseName + ".mp4");
        if (resumeRun)
            return candidate;

        int index = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(rootDir, baseName + "-" + index.ToString(CultureInfo.InvariantCulture) + ".mp4");
            index++;
        }
        return candidate;
    }

    private void PrepareCleanRunDirectory(string runDir, string rootDir)
    {
        if (Directory.Exists(runDir))
            SafeDeleteRunDirectory(runDir, rootDir);
    }

    private void SafeDeleteRunDirectory(string runDir, string rootDir)
    {
        string fullRun = Path.GetFullPath(runDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullRoot = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string requiredPrefix = fullRoot + Path.DirectorySeparatorChar;

        if (fullRun.Length <= fullRoot.Length ||
            !fullRun.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase) ||
            fullRun.IndexOf("-hybrid-range-", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new ApplicationException("Refusing to delete unexpected folder:\r\n" + fullRun);
        }

        Directory.Delete(fullRun, true);
    }

    private void RenderVegasPartsWithTemplate(Vegas vegas, List<Range> ranges, string outDir, double fps, RenderTemplate template,
        TextBox log, string logPath, PipelineStep step, Label current, FragmentProgress fragmentProgress)
    {
        if (ranges.Count == 0)
        {
            AppendProgress(log, logPath, "No VEGAS complex ranges to render.");
            return;
        }

        string vegasDir = Path.Combine(outDir, "vegas-parts");
        Directory.CreateDirectory(vegasDir);
        string manifestPath = Path.Combine(vegasDir, "vegas-parts.csv");
        AppendProgress(log, logPath, "VEGAS complex ranges: " + ranges.Count.ToString(CultureInfo.InvariantCulture));
        AppendProgress(log, logPath, "VEGAS parts folder: " + vegasDir);

        using (StreamWriter manifest = new StreamWriter(manifestPath, false, Encoding.UTF8))
        {
            manifest.WriteLine("index,startFrame,endFrame,frames,startSeconds,endSeconds,file");
            for (int i = 0; i < ranges.Count; i++)
            {
                Range range = ranges[i];
                string outputFile = Path.Combine(vegasDir, String.Format(CultureInfo.InvariantCulture,
                    "vegas_{0:000}_{1}_{2}.mp4", i, range.Start, range.End));
                long frames = range.End - range.Start;
                string label = "VEGAS " + (i + 1).ToString(CultureInfo.InvariantCulture) + "/" +
                    ranges.Count.ToString(CultureInfo.InvariantCulture) + ": frames " +
                    range.Start.ToString(CultureInfo.InvariantCulture) + "-" +
                    range.End.ToString(CultureInfo.InvariantCulture) + " (" +
                    Seconds(frames, fps) + " sec)";

                SetStepProgress(step, i, ranges.Count);
                SetStatusDetail(current, label);
                AppendProgress(log, logPath, label);
                AppendProgress(log, logPath, "  reasons: " + String.Join("; ", range.Reasons.ToArray()));
                AppendProgress(log, logPath, "  output: " + outputFile);

                if (File.Exists(outputFile))
                    File.Delete(outputFile);

                RenderArgs args = new RenderArgs();
                args.OutputFile = outputFile;
                args.RenderTemplate = template;
                args.Start = Timecode.FromFrames(range.Start);
                args.Length = Timecode.FromFrames(range.End - range.Start);
                args.UseSelection = false;
                args.IncludeMarkers = false;
                args.StretchToFill = false;
                args.UseProjectRotation = true;
                args.WaitForIdle = true;
                args.ShowOpenButtonsOnComplete = false;

                RenderStatus status = vegas.Render(args);
                if (status == RenderStatus.Canceled)
                    throw new ApplicationException("VEGAS render canceled.");
                if (status != RenderStatus.Complete)
                    throw new ApplicationException("VEGAS render failed for " +
                        range.Start.ToString(CultureInfo.InvariantCulture) + "-" +
                        range.End.ToString(CultureInfo.InvariantCulture));

                if (!FileReady(outputFile))
                    throw new ApplicationException("VEGAS render reported complete but output is missing or empty:\r\n" + outputFile);
                if (!HasVideoStream(outputFile))
                    throw new ApplicationException("VEGAS render output contains no video stream. Choose a real video render template, not an audio-only template.\r\n\r\nTemplate: " +
                        template.Name + "\r\nFile: " + outputFile);

                double actualSeconds;
                if (TryProbeDurationSeconds(outputFile, out actualSeconds))
                    AppendProgress(log, logPath, "  wrote " + FormatFileSize(outputFile) + ", duration " +
                        Num(actualSeconds) + " sec (expected " + Seconds(frames, fps) + " sec)");
                else
                    AppendProgress(log, logPath, "  wrote " + FormatFileSize(outputFile) + ", duration probe unavailable");

                manifest.WriteLine(String.Join(",",
                    i.ToString(CultureInfo.InvariantCulture),
                    range.Start.ToString(CultureInfo.InvariantCulture),
                    range.End.ToString(CultureInfo.InvariantCulture),
                    frames.ToString(CultureInfo.InvariantCulture),
                    Seconds(range.Start, fps),
                    Seconds(range.End, fps),
                    Csv(outputFile)));
                SetStepProgress(step, i + 1, ranges.Count);
                SetFragmentProgress(fragmentProgress, i + 1);
            }
        }
    }

    private void RenderAudioFromVegasWithTemplate(Vegas vegas, long startFrame, long endFrame, string audioFile, RenderTemplate template,
        TextBox log, string logPath, Label current, double fps)
    {
        double expectedSeconds = ((double)(endFrame - startFrame)) / fps;
        SetStatusDetail(current, "Rendering audio WAV for " + Num(expectedSeconds) + " sec: " + audioFile);
        AppendProgress(log, logPath, "Audio render target: " + audioFile);
        AppendProgress(log, logPath, "Audio expected duration: " + Num(expectedSeconds) + " sec");

        if (File.Exists(audioFile))
            File.Delete(audioFile);

        RenderArgs args = new RenderArgs();
        args.OutputFile = audioFile;
        args.RenderTemplate = template;
        args.Start = Timecode.FromFrames(startFrame);
        args.Length = Timecode.FromFrames(endFrame - startFrame);
        args.UseSelection = false;
        args.IncludeMarkers = false;
        args.StretchToFill = false;
        args.UseProjectRotation = true;
        args.WaitForIdle = true;
        args.ShowOpenButtonsOnComplete = false;

        RenderStatus status = vegas.Render(args);
        if (status == RenderStatus.Canceled)
            throw new ApplicationException("VEGAS audio render canceled.");
        if (status != RenderStatus.Complete)
            throw new ApplicationException("VEGAS audio render failed.");

        if (!FileReady(audioFile))
            throw new ApplicationException("VEGAS audio render reported complete but output is missing or empty:\r\n" + audioFile);

        double actualSeconds;
        if (TryProbeDurationSeconds(audioFile, out actualSeconds))
            AppendProgress(log, logPath, "Audio wrote " + FormatFileSize(audioFile) + ", duration " +
                Num(actualSeconds) + " sec (expected " + Num(expectedSeconds) + " sec)");
        else
            AppendProgress(log, logPath, "Audio wrote " + FormatFileSize(audioFile) + ", duration probe unavailable");
    }

    private void ShowRunWindow(Vegas vegas, string outDir, long startFrame, long endFrame, double fps,
        List<Range> complex, List<SimpleSegment> simple, string ffmpegScript, string assembleScript, string audioFile, string muxScript, string summaryPath)
    {
        Form form = new Form();
        form.Text = "Hybrid render";
        form.StartPosition = FormStartPosition.CenterScreen;
        form.Width = 980;
        form.Height = 730;
        form.MinimizeBox = false;
        form.MaximizeBox = false;

        Label title = new Label();
        title.Left = 12;
        title.Top = 12;
        title.Width = 940;
        title.Height = 40;
        title.Text = "Selection " + startFrame.ToString(CultureInfo.InvariantCulture) + "-" +
            endFrame.ToString(CultureInfo.InvariantCulture) +
            " | VEGAS ranges: " + complex.Count.ToString(CultureInfo.InvariantCulture) +
            " | FFmpeg segments: " + simple.Count.ToString(CultureInfo.InvariantCulture) +
            " | Folder: " + outDir;
        form.Controls.Add(title);

        TextBox log = new TextBox();
        log.Left = 12;
        log.Top = 60;
        log.Width = 940;
        log.Height = 500;
        log.Multiline = true;
        log.ScrollBars = ScrollBars.Both;
        log.WordWrap = false;
        log.ReadOnly = true;
        log.Text = BuildRunText(outDir, startFrame, endFrame, fps, complex, simple, ffmpegScript, assembleScript, audioFile, muxScript, summaryPath);
        form.Controls.Add(log);

        Button renderVegas = new Button();
        renderVegas.Text = "1. Render VEGAS parts";
        renderVegas.Left = 12;
        renderVegas.Top = 580;
        renderVegas.Width = 170;
        form.Controls.Add(renderVegas);

        Button renderFfmpeg = new Button();
        renderFfmpeg.Text = "2. Render FFmpeg parts";
        renderFfmpeg.Left = 192;
        renderFfmpeg.Top = 580;
        renderFfmpeg.Width = 170;
        form.Controls.Add(renderFfmpeg);

        Button assemble = new Button();
        assemble.Text = "3. Assemble video";
        assemble.Left = 372;
        assemble.Top = 580;
        assemble.Width = 145;
        form.Controls.Add(assemble);

        Button renderAudio = new Button();
        renderAudio.Text = "4. Render audio WAV";
        renderAudio.Left = 527;
        renderAudio.Top = 580;
        renderAudio.Width = 160;
        form.Controls.Add(renderAudio);

        Button muxFinal = new Button();
        muxFinal.Text = "5. Mux final MP4";
        muxFinal.Left = 697;
        muxFinal.Top = 580;
        muxFinal.Width = 135;
        form.Controls.Add(muxFinal);

        Button openFolder = new Button();
        openFolder.Text = "Open folder";
        openFolder.Left = 642;
        openFolder.Top = 620;
        openFolder.Width = 120;
        form.Controls.Add(openFolder);

        Button close = new Button();
        close.Text = "Close";
        close.Left = 832;
        close.Top = 620;
        close.Width = 120;
        close.DialogResult = DialogResult.OK;
        form.Controls.Add(close);

        renderVegas.Click += delegate(object sender, EventArgs e)
        {
            try
            {
                AppendLog(log, "Rendering VEGAS parts...");
                RenderVegasParts(vegas, complex, outDir, fps);
                AppendLog(log, "VEGAS parts done.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "VEGAS parts", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        renderFfmpeg.Click += delegate(object sender, EventArgs e)
        {
            try
            {
                AppendLog(log, "Running FFmpeg simple segments script...");
                RunPowerShellScript(ffmpegScript);
                AppendLog(log, "FFmpeg simple segments done.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "FFmpeg parts", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        assemble.Click += delegate(object sender, EventArgs e)
        {
            try
            {
                AppendLog(log, "Running low-load concat/copy assembly...");
                RunPowerShellScript(assembleScript);
                AppendLog(log, "Assembly done.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Assemble video", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        renderAudio.Click += delegate(object sender, EventArgs e)
        {
            try
            {
                AppendLog(log, "Rendering full-range audio from VEGAS...");
                RenderAudioFromVegas(vegas, startFrame, endFrame, audioFile);
                AppendLog(log, "Audio WAV done.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Render audio", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        muxFinal.Click += delegate(object sender, EventArgs e)
        {
            try
            {
                AppendLog(log, "Muxing final MP4 with audio...");
                RunPowerShellScript(muxScript);
                AppendLog(log, "Final MP4 done.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Mux final MP4", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        openFolder.Click += delegate(object sender, EventArgs e)
        {
            Process.Start("explorer.exe", outDir);
        };

        form.ShowDialog();
    }

    private string BuildRunText(string outDir, long startFrame, long endFrame, double fps,
        List<Range> complex, List<SimpleSegment> simple, string ffmpegScript, string assembleScript, string audioFile, string muxScript, string summaryPath)
    {
        StringBuilder text = new StringBuilder();
        text.AppendLine("Hybrid plan created from the current VEGAS selection.");
        text.AppendLine();
        text.AppendLine("Selection frames: " + startFrame.ToString(CultureInfo.InvariantCulture) + "-" + endFrame.ToString(CultureInfo.InvariantCulture));
        text.AppendLine("Selection seconds: " + Seconds(endFrame - startFrame, fps));
        text.AppendLine("Work folder: " + outDir);
        text.AppendLine();
        text.AppendLine("Files:");
        text.AppendLine("  " + summaryPath);
        text.AppendLine("  " + ffmpegScript);
        text.AppendLine("  " + assembleScript);
        text.AppendLine("  " + audioFile);
        text.AppendLine("  " + muxScript);
        text.AppendLine();
        text.AppendLine("VEGAS complex ranges:");
        foreach (Range range in complex)
        {
            text.AppendLine("  " + range.Start.ToString(CultureInfo.InvariantCulture) + "-" +
                range.End.ToString(CultureInfo.InvariantCulture) + " | " +
                String.Join("; ", range.Reasons.ToArray()));
        }
        text.AppendLine();
        text.AppendLine("FFmpeg simple segments:");
        foreach (SimpleSegment segment in simple)
        {
            text.AppendLine("  " + segment.TimelineStart.ToString(CultureInfo.InvariantCulture) + "-" +
                segment.TimelineEnd.ToString(CultureInfo.InvariantCulture) + " | " +
                segment.OutFile);
        }
        return text.ToString();
    }

    private void AppendLog(TextBox log, string line)
    {
        log.AppendText("\r\n[" + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "] " + line);
    }

    private void RunPowerShellScript(string scriptPath)
    {
        if (!File.Exists(scriptPath))
            throw new ApplicationException("Script does not exist:\r\n" + scriptPath);

        ProcessStartInfo info = new ProcessStartInfo();
        info.FileName = "powershell.exe";
        info.Arguments = "-NoProfile -ExecutionPolicy Bypass -File " + CmdQuote(scriptPath);
        info.WorkingDirectory = Path.GetDirectoryName(scriptPath);
        info.UseShellExecute = true;
        info.WindowStyle = ProcessWindowStyle.Normal;

        Process process = Process.Start(info);
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new ApplicationException("PowerShell script failed with exit code " +
                process.ExitCode.ToString(CultureInfo.InvariantCulture) + ":\r\n" + scriptPath);
    }

    private void RenderVegasParts(Vegas vegas, List<Range> ranges, string outDir, double fps)
    {
        if (ranges.Count == 0)
        {
            MessageBox.Show("No VEGAS ranges to render.", "VEGAS parts", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        RenderTemplate template = ChooseRenderTemplate(vegas);
        if (template == null)
            return;

        if (!ConfirmVegasRender(ranges, fps, template))
            return;

        string vegasDir = Path.Combine(outDir, "vegas-parts");
        Directory.CreateDirectory(vegasDir);
        string manifestPath = Path.Combine(vegasDir, "vegas-parts.csv");

        using (StreamWriter manifest = new StreamWriter(manifestPath, false, Encoding.UTF8))
        {
            manifest.WriteLine("index,startFrame,endFrame,frames,startSeconds,endSeconds,file");
            for (int i = 0; i < ranges.Count; i++)
            {
                Range range = ranges[i];
                string outputFile = Path.Combine(vegasDir, String.Format(CultureInfo.InvariantCulture,
                    "vegas_{0:000}_{1}_{2}.mp4", i, range.Start, range.End));

                if (!File.Exists(outputFile))
                {
                    RenderArgs args = new RenderArgs();
                    args.OutputFile = outputFile;
                    args.RenderTemplate = template;
                    args.Start = Timecode.FromFrames(range.Start);
                    args.Length = Timecode.FromFrames(range.End - range.Start);
                    args.UseSelection = false;
                    args.IncludeMarkers = false;
                    args.StretchToFill = false;
                    args.UseProjectRotation = true;
                    args.WaitForIdle = true;
                    args.ShowOpenButtonsOnComplete = false;

                    RenderStatus status = vegas.Render(args);
                    if (status == RenderStatus.Canceled)
                        break;
                    if (status != RenderStatus.Complete)
                        throw new ApplicationException("VEGAS render failed for " +
                            range.Start.ToString(CultureInfo.InvariantCulture) + "-" +
                            range.End.ToString(CultureInfo.InvariantCulture));
                }

                manifest.WriteLine(String.Join(",",
                    i.ToString(CultureInfo.InvariantCulture),
                    range.Start.ToString(CultureInfo.InvariantCulture),
                    range.End.ToString(CultureInfo.InvariantCulture),
                    (range.End - range.Start).ToString(CultureInfo.InvariantCulture),
                    Seconds(range.Start, fps),
                    Seconds(range.End, fps),
                    Csv(outputFile)));
            }
        }
    }

    private void RenderAudioFromVegas(Vegas vegas, long startFrame, long endFrame, string audioFile)
    {
        RenderTemplate template = ChooseAudioRenderTemplate(vegas);
        if (template == null)
            return;

        if (File.Exists(audioFile))
        {
            DialogResult overwrite = MessageBox.Show(
                "Audio file already exists. Overwrite it?\r\n\r\n" + audioFile,
                "Render audio",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (overwrite != DialogResult.Yes)
                return;
            File.Delete(audioFile);
        }

        RenderArgs args = new RenderArgs();
        args.OutputFile = audioFile;
        args.RenderTemplate = template;
        args.Start = Timecode.FromFrames(startFrame);
        args.Length = Timecode.FromFrames(endFrame - startFrame);
        args.UseSelection = false;
        args.IncludeMarkers = false;
        args.StretchToFill = false;
        args.UseProjectRotation = true;
        args.WaitForIdle = true;
        args.ShowOpenButtonsOnComplete = false;

        RenderStatus status = vegas.Render(args);
        if (status == RenderStatus.Canceled)
            return;
        if (status != RenderStatus.Complete)
            throw new ApplicationException("VEGAS audio render failed.");
    }

    private RenderTemplate ChooseAudioRenderTemplate(Vegas vegas)
    {
        List<RenderChoice> allChoices = new List<RenderChoice>();
        foreach (Renderer renderer in vegas.Renderers)
        {
            if (renderer == null || !renderer.IsActive)
                continue;

            bool audioRenderer = false;
            try
            {
                audioRenderer = renderer.SupportsAudio;
            }
            catch
            {
                audioRenderer = false;
            }

            string rendererText = (renderer.Name + " " + renderer.FileTypeName + " " + renderer.FileExtension).ToLowerInvariant();
            if (!audioRenderer && rendererText.IndexOf("wav") < 0 && rendererText.IndexOf("wave") < 0)
                continue;

            foreach (RenderTemplate template in renderer.Templates)
            {
                if (template != null && template.IsValid())
                    allChoices.Add(new RenderChoice(renderer, template));
            }
        }

        if (allChoices.Count == 0)
            throw new ApplicationException("No valid audio render templates found.");

        Form form = new Form();
        form.Text = "Select render template for audio WAV";
        form.StartPosition = FormStartPosition.CenterScreen;
        form.Width = 980;
        form.Height = 620;
        form.MinimizeBox = false;
        form.MaximizeBox = false;

        Label label = new Label();
        label.Left = 12;
        label.Top = 12;
        label.Width = 930;
        label.Height = 36;
        label.Text = "Choose an audio render template. WAV is recommended because final MP4 muxing will encode AAC once.";
        form.Controls.Add(label);

        TextBox filter = new TextBox();
        filter.Left = 12;
        filter.Top = 54;
        filter.Width = 940;
        filter.Text = "wav";
        form.Controls.Add(filter);

        ListBox list = new ListBox();
        list.Left = 12;
        list.Top = 84;
        list.Width = 940;
        list.Height = 430;
        list.HorizontalScrollbar = true;
        form.Controls.Add(list);

        Button showWave = new Button();
        showWave.Text = "WAV filter";
        showWave.Left = 12;
        showWave.Top = 530;
        showWave.Width = 90;
        form.Controls.Add(showWave);

        Button showAll = new Button();
        showAll.Text = "Show all";
        showAll.Left = 112;
        showAll.Top = 530;
        showAll.Width = 90;
        form.Controls.Add(showAll);

        Button ok = new Button();
        ok.Text = "Use selected";
        ok.Left = 682;
        ok.Top = 530;
        ok.Width = 130;
        ok.DialogResult = DialogResult.OK;
        form.Controls.Add(ok);

        Button cancel = new Button();
        cancel.Text = "Cancel";
        cancel.Left = 822;
        cancel.Top = 530;
        cancel.Width = 130;
        cancel.DialogResult = DialogResult.Cancel;
        form.Controls.Add(cancel);

        form.AcceptButton = ok;
        form.CancelButton = cancel;

        EventHandler refresh = delegate(object sender, EventArgs e)
        {
            string[] words = filter.Text.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            list.Items.Clear();
            foreach (RenderChoice choice in allChoices)
            {
                string haystack = choice.ToString();
                bool match = true;
                foreach (string word in words)
                {
                    if (haystack.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                    list.Items.Add(choice);
            }
            SelectPreferredAudioTemplate(list);
        };

        filter.TextChanged += refresh;
        showWave.Click += delegate(object sender, EventArgs e) { filter.Text = "wav"; };
        showAll.Click += delegate(object sender, EventArgs e) { filter.Text = ""; };
        list.DoubleClick += delegate(object sender, EventArgs e)
        {
            if (list.SelectedItem != null)
                form.DialogResult = DialogResult.OK;
        };
        refresh(null, EventArgs.Empty);

        if (form.ShowDialog() != DialogResult.OK)
            return null;
        if (list.SelectedItem == null)
            throw new ApplicationException("No audio render template selected.");

        return ((RenderChoice)list.SelectedItem).Template;
    }

    private void SelectPreferredAudioTemplate(ListBox list)
    {
        if (list.Items.Count == 0)
            return;

        int bestIndex = 0;
        int bestScore = Int32.MinValue;
        for (int i = 0; i < list.Items.Count; i++)
        {
            RenderChoice choice = (RenderChoice)list.Items[i];
            string text = choice.ToString().ToLowerInvariant();
            int score = 0;

            if (text.IndexOf("wave (microsoft)") >= 0)
                score += 100;
            if (text.IndexOf("48 000") >= 0 || text.IndexOf("48000") >= 0 || text.IndexOf("48,000") >= 0)
                score += 50;
            if (text.IndexOf("16") >= 0 && (text.IndexOf("bit") >= 0 || text.IndexOf("бит") >= 0))
                score += 40;
            if (text.IndexOf("stereo") >= 0 || text.IndexOf("стерео") >= 0)
                score += 30;
            if (text.IndexOf("pcm") >= 0)
                score += 20;
            if (text.IndexOf("float") >= 0 || text.IndexOf("плавающей") >= 0 || text.IndexOf("5.1") >= 0 || text.IndexOf("mono") >= 0 || text.IndexOf("моно") >= 0)
                score -= 100;
            if (text.IndexOf("sony wave64") >= 0 || text.IndexOf("scott studios") >= 0)
                score -= 80;

            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }

        list.SelectedIndex = bestIndex;
    }

    private bool ConfirmVegasRender(List<Range> ranges, double fps, RenderTemplate template)
    {
        long totalFrames = 0;
        StringBuilder msg = new StringBuilder();
        msg.AppendLine("Render only these VEGAS frame ranges?");
        msg.AppendLine();
        for (int i = 0; i < ranges.Count; i++)
        {
            Range range = ranges[i];
            long frames = range.End - range.Start;
            totalFrames += frames;
            if (i < 20)
            {
                msg.AppendLine(String.Format(CultureInfo.InvariantCulture,
                    "{0:000}: {1}-{2} ({3} frames, {4} sec)",
                    i, range.Start, range.End, frames, Seconds(frames, fps)));
            }
        }
        if (ranges.Count > 20)
            msg.AppendLine("... " + (ranges.Count - 20).ToString(CultureInfo.InvariantCulture) + " more ranges");
        msg.AppendLine();
        msg.AppendLine("Total: " + totalFrames.ToString(CultureInfo.InvariantCulture) + " frames, " + Seconds(totalFrames, fps) + " sec");
        msg.AppendLine("Template: " + template.Name);
        msg.AppendLine();
        msg.AppendLine("Existing part files are kept and skipped.");

        return MessageBox.Show(msg.ToString(), "Confirm VEGAS range render",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.OK;
    }

    private RenderTemplate ChooseRenderTemplate(Vegas vegas)
    {
        List<RenderChoice> allChoices = new List<RenderChoice>();
        foreach (Renderer renderer in vegas.Renderers)
        {
            if (renderer == null || !renderer.IsActive)
                continue;
            foreach (RenderTemplate template in renderer.Templates)
            {
                if (template != null && template.IsValid())
                    allChoices.Add(new RenderChoice(renderer, template));
            }
        }

        if (allChoices.Count == 0)
            throw new ApplicationException("No valid VEGAS render templates found.");

        Form form = new Form();
        form.Text = "Select render template for VEGAS parts";
        form.StartPosition = FormStartPosition.CenterScreen;
        form.Width = 980;
        form.Height = 620;
        form.MinimizeBox = false;
        form.MaximizeBox = false;

        Label label = new Label();
        label.Left = 12;
        label.Top = 12;
        label.Width = 930;
        label.Height = 36;
        label.Text = "Choose the VEGAS render template for complex frame ranges. Built-in and custom templates exposed by VEGAS are listed here.";
        form.Controls.Add(label);

        TextBox filter = new TextBox();
        filter.Left = 12;
        filter.Top = 54;
        filter.Width = 940;
        filter.Text = "";
        form.Controls.Add(filter);

        ListBox list = new ListBox();
        list.Left = 12;
        list.Top = 84;
        list.Width = 940;
        list.Height = 430;
        list.HorizontalScrollbar = true;
        form.Controls.Add(list);

        Button showNv = new Button();
        showNv.Text = "NV filter";
        showNv.Left = 12;
        showNv.Top = 530;
        showNv.Width = 90;
        form.Controls.Add(showNv);

        Button showAll = new Button();
        showAll.Text = "Show all";
        showAll.Left = 112;
        showAll.Top = 530;
        showAll.Width = 90;
        form.Controls.Add(showAll);

        Button ok = new Button();
        ok.Text = "Use selected";
        ok.Left = 682;
        ok.Top = 530;
        ok.Width = 130;
        ok.DialogResult = DialogResult.OK;
        form.Controls.Add(ok);

        Button cancel = new Button();
        cancel.Text = "Cancel";
        cancel.Left = 822;
        cancel.Top = 530;
        cancel.Width = 130;
        cancel.DialogResult = DialogResult.Cancel;
        form.Controls.Add(cancel);

        form.AcceptButton = ok;
        form.CancelButton = cancel;

        EventHandler refresh = delegate(object sender, EventArgs e)
        {
            string[] words = filter.Text.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            list.Items.Clear();
            foreach (RenderChoice choice in allChoices)
            {
                string haystack = choice.ToString();
                bool match = true;
                foreach (string word in words)
                {
                    if (haystack.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                    list.Items.Add(choice);
            }
            if (list.Items.Count > 0)
                list.SelectedIndex = 0;
        };

        filter.TextChanged += refresh;
        showNv.Click += delegate(object sender, EventArgs e) { filter.Text = "NV"; };
        showAll.Click += delegate(object sender, EventArgs e) { filter.Text = ""; };
        list.DoubleClick += delegate(object sender, EventArgs e)
        {
            if (list.SelectedItem != null)
                form.DialogResult = DialogResult.OK;
        };
        refresh(null, EventArgs.Empty);

        if (form.ShowDialog() != DialogResult.OK)
            return null;
        if (list.SelectedItem == null)
            throw new ApplicationException("No render template selected.");

        return ((RenderChoice)list.SelectedItem).Template;
    }

    private List<VideoEventInfo> CollectVideoEvents(Vegas vegas, long startFrame, long endFrame)
    {
        List<VideoEventInfo> result = new List<VideoEventInfo>();
        foreach (Track track in vegas.Project.Tracks)
        {
            if (track.MediaType != MediaType.Video || track.Mute)
                continue;

            foreach (TrackEvent ev in track.Events)
            {
                if (!(ev is VideoEvent))
                    continue;
                long s = ev.Start.FrameCount;
                long e = ev.End.FrameCount;
                if (e <= startFrame || s >= endFrame)
                    continue;
                result.Add(new VideoEventInfo((VideoTrack)track, (VideoEvent)ev));
            }
        }
        result.Sort(delegate(VideoEventInfo a, VideoEventInfo b)
        {
            int c = a.StartFrame.CompareTo(b.StartFrame);
            if (c != 0) return c;
            return a.EndFrame.CompareTo(b.EndFrame);
        });
        return result;
    }

    private List<Range> FindComplexRanges(Vegas vegas, List<VideoEventInfo> events, long startFrame, long endFrame)
    {
        List<Range> ranges = new List<Range>();

        foreach (VideoEventInfo info in events)
        {
            VideoTrack track = info.Track;
            VideoEvent ev = info.Event;

            if (HasActiveEffects(track.Effects))
                AddClipped(ranges, info.StartFrame, info.EndFrame, startFrame, endFrame, "video track effects");

            if (ev.PlaybackRate != 1.0)
                AddClipped(ranges, info.StartFrame, info.EndFrame, startFrame, endFrame, "playback rate " + ev.PlaybackRate.ToString("0.#########", CultureInfo.InvariantCulture));

            long fadeIn = ev.FadeIn.Length.FrameCount;
            if (fadeIn > 0)
                AddClipped(ranges, info.StartFrame, info.StartFrame + fadeIn, startFrame, endFrame, "event fade-in/edge");

            long fadeOut = ev.FadeOut.Length.FrameCount;
            if (fadeOut > 0)
                AddClipped(ranges, info.EndFrame - fadeOut, info.EndFrame, startFrame, endFrame, "event fade-out/edge");

            if (HasActiveEffects(ev.Effects))
                AddClipped(ranges, info.StartFrame, info.EndFrame, startFrame, endFrame, "video event effects");

            if (ev.ActiveTake == null || ev.ActiveTake.Media == null || !IsSimpleVideoPath(ev.ActiveTake.MediaPath))
                AddClipped(ranges, info.StartFrame, info.EndFrame, startFrame, endFrame, "non-simple source media");
            else if (HasActiveEffects(ev.ActiveTake.Media.Effects))
                AddClipped(ranges, info.StartFrame, info.EndFrame, startFrame, endFrame, "media effects");

            if (IsComplexVideoMotion(ev))
                AddClipped(ranges, info.StartFrame, info.EndFrame, startFrame, endFrame, "video motion");
        }

        for (int i = 0; i < events.Count; i++)
        {
            for (int j = i + 1; j < events.Count; j++)
            {
                if (events[j].StartFrame >= events[i].EndFrame)
                    break;

                long s = Math.Max(events[i].StartFrame, events[j].StartFrame);
                long e = Math.Min(events[i].EndFrame, events[j].EndFrame);
                AddClipped(ranges, s, e, startFrame, endFrame, "overlap events " + events[i].Event.Index + "-" + events[j].Event.Index);
            }
        }

        return ranges;
    }

    private List<SimpleSegment> BuildSimpleSegments(List<VideoEventInfo> events, List<Range> complex, long startFrame, long endFrame, double fps, string outDir)
    {
        List<SimpleSegment> result = new List<SimpleSegment>();
        long cursor = startFrame;
        int index = 0;

        foreach (Range range in complex)
        {
            if (cursor < range.Start)
            {
                index = AddSimpleSegmentsInInterval(result, events, cursor, range.Start, fps, outDir, index);
            }
            if (range.End > cursor)
                cursor = range.End;
        }
        if (cursor < endFrame)
            AddSimpleSegmentsInInterval(result, events, cursor, endFrame, fps, outDir, index);

        return result;
    }

    private int AddSimpleSegmentsInInterval(List<SimpleSegment> result, List<VideoEventInfo> events, long start, long end, double fps, string outDir, int index)
    {
        long cursor = start;
        while (cursor < end)
        {
            VideoEventInfo owner = FindSimpleOwnerAt(events, cursor);
            if (owner == null)
            {
                long next = FindNextSimpleOwnerStart(events, cursor, end);
                if (next <= cursor)
                    next = end;
                cursor = next;
                continue;
            }

            long segmentEnd = Math.Min(end, owner.EndFrame);
            if (segmentEnd <= cursor)
            {
                cursor++;
                continue;
            }

            AddSimple(result, owner, cursor, segmentEnd, outDir, index);
            index++;
            cursor = segmentEnd;
        }
        return index;
    }

    private List<Range> PreserveUncopyableTimelineRanges(List<VideoEventInfo> events, List<Range> complex, long startFrame, long endFrame)
    {
        List<Range> result = new List<Range>();
        foreach (Range range in complex)
            result.Add(range.Clone());

        long cursor = startFrame;
        foreach (Range range in complex)
        {
            if (cursor < range.Start && FindSimpleOwner(events, cursor, range.Start) == null)
                AddUncopyableRangesInInterval(result, events, cursor, range.Start);
            if (range.End > cursor)
                cursor = range.End;
        }

        if (cursor < endFrame && FindSimpleOwner(events, cursor, endFrame) == null)
            AddUncopyableRangesInInterval(result, events, cursor, endFrame);

        return MergeRanges(result);
    }

    private bool AddSimple(List<SimpleSegment> result, List<VideoEventInfo> events, long start, long end, double fps, string outDir, int index)
    {
        VideoEventInfo owner = FindSimpleOwner(events, start, end);
        if (owner == null)
        {
            return false;
        }

        long sourceStart = owner.Event.ActiveTake.Offset.FrameCount + (start - owner.StartFrame);
        long sourceEnd = owner.Event.ActiveTake.Offset.FrameCount + (end - owner.StartFrame);
        string outFile = Path.Combine(outDir, String.Format(CultureInfo.InvariantCulture, "simple_{0:000}_{1}_{2}.mp4", index, start, end));
        result.Add(new SimpleSegment(index, start, end, sourceStart, sourceEnd, owner.Event.ActiveTake.MediaPath, outFile));
        return true;
    }

    private void AddSimple(List<SimpleSegment> result, VideoEventInfo owner, long start, long end, string outDir, int index)
    {
        long sourceStart = owner.Event.ActiveTake.Offset.FrameCount + (start - owner.StartFrame);
        long sourceEnd = owner.Event.ActiveTake.Offset.FrameCount + (end - owner.StartFrame);
        string outFile = Path.Combine(outDir, String.Format(CultureInfo.InvariantCulture, "simple_{0:000}_{1}_{2}.mp4", index, start, end));
        result.Add(new SimpleSegment(index, start, end, sourceStart, sourceEnd, owner.Event.ActiveTake.MediaPath, outFile));
    }

    private void AddUncopyableRangesInInterval(List<Range> result, List<VideoEventInfo> events, long start, long end)
    {
        long cursor = start;
        while (cursor < end)
        {
            VideoEventInfo owner = FindSimpleOwnerAt(events, cursor);
            if (owner != null)
            {
                long next = Math.Min(end, owner.EndFrame);
                cursor = next > cursor ? next : cursor + 1;
                continue;
            }

            long gapEnd = FindNextSimpleOwnerStart(events, cursor, end);
            if (gapEnd <= cursor)
                gapEnd = end;
            result.Add(new Range(cursor, gapEnd, "timeline gap/no simple video source"));
            cursor = gapEnd;
        }
    }

    private VideoEventInfo FindSimpleOwner(List<VideoEventInfo> events, long start, long end)
    {
        long mid = (start + end) / 2;
        foreach (VideoEventInfo info in events)
        {
            if (info.StartFrame <= mid && info.EndFrame >= end && IsSimpleOwner(info))
                return info;
        }
        return null;
    }

    private VideoEventInfo FindSimpleOwnerAt(List<VideoEventInfo> events, long frame)
    {
        foreach (VideoEventInfo info in events)
        {
            if (info.StartFrame <= frame && frame < info.EndFrame && IsSimpleOwner(info))
                return info;
        }
        return null;
    }

    private long FindNextSimpleOwnerStart(List<VideoEventInfo> events, long start, long end)
    {
        long next = end;
        foreach (VideoEventInfo info in events)
        {
            if (info.StartFrame > start && info.StartFrame < next && IsSimpleOwner(info))
                next = info.StartFrame;
        }
        return next;
    }

    private bool IsSimpleOwner(VideoEventInfo info)
    {
        return info != null &&
            info.Event.PlaybackRate == 1.0 &&
            info.Event.ActiveTake != null &&
            IsSimpleVideoPath(info.Event.ActiveTake.MediaPath);
    }

    private bool IsComplexVideoMotion(VideoEvent ev)
    {
        try
        {
            if (ev.VideoMotion == null || ev.VideoMotion.Keyframes == null)
                return false;
            if (ev.VideoMotion.Keyframes.Count != 1)
                return true;
            VideoMotionKeyframe keyframe = ev.VideoMotion.Keyframes[0];
            if (keyframe.Rotation != 0.0)
                return true;
        }
        catch
        {
            return true;
        }
        return false;
    }

    private bool HasActiveEffects(Effects effects)
    {
        if (effects == null || effects.Count == 0)
            return false;
        for (int i = 0; i < effects.Count; i++)
        {
            try
            {
                if (!effects[i].Bypass)
                    return true;
            }
            catch
            {
                return true;
            }
        }
        return false;
    }

    private bool IsSimpleVideoPath(string path)
    {
        if (String.IsNullOrEmpty(path))
            return false;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".mp4" || ext == ".mov" || ext == ".mkv" || ext == ".avi" || ext == ".m2ts" || ext == ".mts";
    }

    private void AddClipped(List<Range> ranges, long start, long end, long clipStart, long clipEnd, string reason)
    {
        long s = Math.Max(start, clipStart);
        long e = Math.Min(end, clipEnd);
        if (e > s)
            ranges.Add(new Range(s, e, reason));
    }

    private List<Range> MergeRanges(List<Range> ranges)
    {
        ranges.Sort(delegate(Range a, Range b)
        {
            int c = a.Start.CompareTo(b.Start);
            if (c != 0) return c;
            return a.End.CompareTo(b.End);
        });

        List<Range> merged = new List<Range>();
        foreach (Range range in ranges)
        {
            if (range.End <= range.Start)
                continue;
            if (merged.Count == 0 || range.Start > merged[merged.Count - 1].End)
            {
                merged.Add(range.Clone());
            }
            else
            {
                Range current = merged[merged.Count - 1];
                if (range.End > current.End)
                    current.End = range.End;
                foreach (string reason in range.Reasons)
                {
                    if (!current.Reasons.Contains(reason))
                        current.Reasons.Add(reason);
                }
            }
        }
        return merged;
    }

    private void WriteComplexCsv(string path, List<Range> ranges, double fps)
    {
        using (StreamWriter writer = new StreamWriter(path, false, Encoding.UTF8))
        {
            writer.WriteLine("startFrame,endFrame,frames,startSec,endSec,seconds,reasons");
            foreach (Range range in ranges)
            {
                writer.WriteLine(String.Join(",",
                    range.Start.ToString(CultureInfo.InvariantCulture),
                    range.End.ToString(CultureInfo.InvariantCulture),
                    (range.End - range.Start).ToString(CultureInfo.InvariantCulture),
                    Seconds(range.Start, fps),
                    Seconds(range.End, fps),
                    Seconds(range.End - range.Start, fps),
                    Csv(String.Join("; ", range.Reasons.ToArray()))));
            }
        }
    }

    private void WriteSimpleCsv(string path, List<SimpleSegment> simple)
    {
        using (StreamWriter writer = new StreamWriter(path, false, Encoding.UTF8))
        {
            writer.WriteLine("index,timelineStartFrame,timelineEndFrame,timelineFrames,sourceFile,sourceStartFrame,sourceEndFrame,outFile");
            foreach (SimpleSegment segment in simple)
            {
                writer.WriteLine(String.Join(",",
                    segment.Index.ToString(CultureInfo.InvariantCulture),
                    segment.TimelineStart.ToString(CultureInfo.InvariantCulture),
                    segment.TimelineEnd.ToString(CultureInfo.InvariantCulture),
                    (segment.TimelineEnd - segment.TimelineStart).ToString(CultureInfo.InvariantCulture),
                    Csv(segment.SourceFile),
                    segment.SourceStart.ToString(CultureInfo.InvariantCulture),
                    segment.SourceEnd.ToString(CultureInfo.InvariantCulture),
                    Csv(segment.OutFile)));
            }
        }
    }

    private void WriteVegasRanges(string path, List<Range> ranges)
    {
        using (StreamWriter writer = new StreamWriter(path, false, Encoding.UTF8))
        {
            foreach (Range range in ranges)
                writer.WriteLine(range.Start.ToString(CultureInfo.InvariantCulture) + "-" + range.End.ToString(CultureInfo.InvariantCulture));
        }
    }

    private void WriteFfmpegScript(string path, List<SimpleSegment> simple, double fps)
    {
        using (StreamWriter writer = new StreamWriter(path, false, Encoding.UTF8))
        {
            writer.WriteLine("$ErrorActionPreference = \"Stop\"");
            WritePowerShellUtf8Header(writer);
            writer.WriteLine("# Generated from current VEGAS timeline selection.");
            writer.WriteLine("Write-Host " + PsQuote("FFmpeg simple segment plan: " + simple.Count.ToString(CultureInfo.InvariantCulture) + " segment(s)."));
            foreach (SimpleSegment segment in simple)
            {
                double sourceStartSec = ((double)segment.SourceStart) / fps;
                double durationSec = ((double)(segment.TimelineEnd - segment.TimelineStart)) / fps;
                double seek = Math.Max(0.0, sourceStartSec - 5.0);
                double trimStart = sourceStartSec - seek;
                string vf = "trim=start=" + Num(trimStart) + ":duration=" + Num(durationSec) + ",setpts=PTS-STARTPTS,fps=" + Num(fps) + ",format=yuv420p";
                writer.WriteLine("Write-Host " + PsQuote("FFmpeg simple " + (segment.Index + 1).ToString(CultureInfo.InvariantCulture) + "/" +
                    simple.Count.ToString(CultureInfo.InvariantCulture) + ": frames " +
                    segment.TimelineStart.ToString(CultureInfo.InvariantCulture) + "-" +
                    segment.TimelineEnd.ToString(CultureInfo.InvariantCulture) + " (" + Num(durationSec) + " sec)"));
                writer.WriteLine("Write-Host " + PsQuote("  source: " + segment.SourceFile));
                writer.WriteLine("Write-Host " + PsQuote("  output: " + segment.OutFile));
                writer.WriteLine(
                    "ffmpeg -y -hide_banner -hwaccel cuda -ss " + Num(seek) +
                    " -i " + PsQuote(segment.SourceFile) +
                    " -an -vf " + PsQuote(vf) +
                    " -frames:v " + (segment.TimelineEnd - segment.TimelineStart).ToString(CultureInfo.InvariantCulture) +
                    " -c:v h264_nvenc -preset p5 -cq 18 -profile:v high -pix_fmt yuv420p -video_track_timescale 60000 " + PsQuote(segment.OutFile));
                writer.WriteLine("if ($LASTEXITCODE -ne 0) { throw \"ffmpeg simple segment " +
                    segment.Index.ToString(CultureInfo.InvariantCulture) + " failed with exit code $LASTEXITCODE\" }");
                writer.WriteLine("if (-not (Test-Path -LiteralPath " + PsQuote(segment.OutFile) + ")) { throw " +
                    PsQuote("Missing ffmpeg segment output: " + segment.OutFile) + " }");
                writer.WriteLine("Write-Host (\"  output size: {0:n0} bytes\" -f (Get-Item -LiteralPath " + PsQuote(segment.OutFile) + ").Length)");
                writer.WriteLine("Write-Host " + PsQuote("FFmpeg simple done " + (segment.Index + 1).ToString(CultureInfo.InvariantCulture) + "/" +
                    simple.Count.ToString(CultureInfo.InvariantCulture)));
            }
        }
    }

    private void WriteAssembleScript(string path, List<SimpleSegment> simple, List<Range> complex, double fps, string outDir, string target = null)
    {
        string vegasDir = Path.Combine(outDir, "vegas-parts");
        string listPath = target == null ? Path.Combine(outDir, "parts-video-only.ffconcat") : Path.ChangeExtension(path, ".ffconcat");
        string outFile = target ?? Path.Combine(outDir, "hybrid_video_only.mp4");

        List<PartInfo> parts = BuildOrderedParts(simple, complex, vegasDir);
        using (StreamWriter writer = new StreamWriter(path, false, Encoding.UTF8))
        {
            writer.WriteLine("$ErrorActionPreference = \"Stop\"");
            WritePowerShellUtf8Header(writer);
            writer.WriteLine("# Low-load final assembly. This should copy video streams instead of re-rendering.");
            writer.WriteLine("$list = " + PsQuote(listPath));
            writer.WriteLine("$out = " + PsQuote(outFile));
            writer.WriteLine("function Test-VideoStream([string]$path) {");
            writer.WriteLine("    $probe = & ffprobe -v error -select_streams v:0 -show_entries stream=codec_type -of default=noprint_wrappers=1:nokey=1 $path 2>&1");
            writer.WriteLine("    if ($LASTEXITCODE -ne 0) { throw \"ffprobe failed for video part: $path`r`n$probe\" }");
            writer.WriteLine("    return ($probe -match '^video$')");
            writer.WriteLine("}");
            writer.WriteLine("Write-Host " + PsQuote("Assembling video-only file from " + parts.Count.ToString(CultureInfo.InvariantCulture) + " part(s)."));
            writer.WriteLine("$lines = @('ffconcat version 1.0')");
            writer.WriteLine("[Int64]$sourceBytes = 0");
            for (int i = 0; i < parts.Count; i++)
            {
                PartInfo part = parts[i];
                writer.WriteLine("Write-Host " + PsQuote("Part " + (i + 1).ToString(CultureInfo.InvariantCulture) + "/" +
                    parts.Count.ToString(CultureInfo.InvariantCulture) + ": frames " +
                    part.Start.ToString(CultureInfo.InvariantCulture) + "-" +
                    part.End.ToString(CultureInfo.InvariantCulture) + " -> " + part.FilePath));
                writer.WriteLine("if (-not (Test-Path -LiteralPath " + PsQuote(part.FilePath) + ")) { throw " +
                    PsQuote("Missing video part: " + part.FilePath) + " }");
                writer.WriteLine("if (-not (Test-VideoStream " + PsQuote(part.FilePath) + ")) { throw " +
                    PsQuote("Video part has no video stream: " + part.FilePath) + " }");
                writer.WriteLine("$sourceBytes += (Get-Item -LiteralPath " + PsQuote(part.FilePath) + ").Length");
                writer.WriteLine("$lines += \"file '" + EscapeFfconcat(part.FilePath) + "'\"");
            }
            writer.WriteLine("$drive = [System.IO.DriveInfo]::new([System.IO.Path]::GetPathRoot($out))");
            writer.WriteLine("$required = [Int64]($sourceBytes + [Math]::Max([double]256MB, [Math]::Ceiling($sourceBytes * 0.01)))");
            writer.WriteLine("$free = $drive.AvailableFreeSpace");
            writer.WriteLine("Write-Host ('Assembly space on {0}: {1:N2} GiB free, {2:N2} GiB required.' -f $drive.Name, ($free / 1GB), ($required / 1GB))");
            writer.WriteLine("if ($free -lt $required) { throw ('Not enough disk space on {0} for video assembly: {1:N2} GiB free, {2:N2} GiB required. Free space or select another temporary folder.' -f $drive.Name, ($free / 1GB), ($required / 1GB)) }");
            writer.WriteLine("[System.IO.File]::WriteAllLines($list, $lines, [System.Text.UTF8Encoding]::new($false))");
            writer.WriteLine("$pending = $out + '.' + [Guid]::NewGuid().ToString('N') + '.assembling.mp4'");
            writer.WriteLine("try {");
            writer.WriteLine("    ffmpeg -n -nostdin -hide_banner -f concat -safe 0 -i $list -map 0:v:0 -an -c:v copy $pending");
            writer.WriteLine("    if ($LASTEXITCODE -ne 0) { throw \"ffmpeg stream-copy assembly failed with exit code $LASTEXITCODE\" }");
            writer.WriteLine("    if (-not (Test-VideoStream $pending)) { throw 'Assembled output has no video stream.' }");
            writer.WriteLine("    if (Test-Path -LiteralPath $out) { [System.IO.File]::Replace($pending, $out, [NullString]::Value) }");
            writer.WriteLine("    else { [System.IO.File]::Move($pending, $out) }");
            writer.WriteLine("} finally {");
            writer.WriteLine("    if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending -Force -ErrorAction SilentlyContinue }");
            writer.WriteLine("}");
            writer.WriteLine("Write-Host (\"Video-only output size: {0:n0} bytes\" -f (Get-Item -LiteralPath $out).Length)");
            writer.WriteLine("Write-Host " + PsQuote("Video-only output ready: ") + " $out");
        }
    }

    private void WriteMuxScript(string path, string outDir, string audioFile, string finalFile, double expectedDurationSeconds, string videoTarget = null)
    {
        string videoFile = videoTarget ?? Path.Combine(outDir, "hybrid_video_only.mp4");

        using (StreamWriter writer = new StreamWriter(path, false, Encoding.UTF8))
        {
            writer.WriteLine("$ErrorActionPreference = \"Stop\"");
            WritePowerShellUtf8Header(writer);
            writer.WriteLine("$video = " + PsQuote(videoFile));
            writer.WriteLine("$audio = " + PsQuote(audioFile));
            writer.WriteLine("$out = " + PsQuote(finalFile));
            writer.WriteLine("$tmp = $out + '.' + [Guid]::NewGuid().ToString('N') + '.muxing.mp4'");
            writer.WriteLine("$expectedSeconds = " + Num(expectedDurationSeconds));
            writer.WriteLine("function Format-Bytes([Int64]$bytes) {");
            writer.WriteLine("    if ($bytes -ge 1GB) { return (\"{0:n2} GB\" -f ($bytes / 1GB)) }");
            writer.WriteLine("    if ($bytes -ge 1MB) { return (\"{0:n2} MB\" -f ($bytes / 1MB)) }");
            writer.WriteLine("    return (\"{0:n0} bytes\" -f $bytes)");
            writer.WriteLine("}");
            writer.WriteLine("if (-not (Test-Path -LiteralPath $video)) { throw \"Missing assembled video: $video\" }");
            writer.WriteLine("if (-not (Test-Path -LiteralPath $audio)) { throw \"Missing VEGAS audio WAV: $audio\" }");
            writer.WriteLine("Write-Host " + PsQuote("Muxing final MP4. Expected duration: " + Num(expectedDurationSeconds) + " sec."));
            writer.WriteLine("Write-Host " + PsQuote("  video: " + videoFile));
            writer.WriteLine("Write-Host " + PsQuote("  audio: " + audioFile));
            writer.WriteLine("Write-Host " + PsQuote("  output: " + finalFile));
            writer.WriteLine("$videoInfo = Get-Item -LiteralPath $video");
            writer.WriteLine("$audioInfo = Get-Item -LiteralPath $audio");
            writer.WriteLine("Write-Host (\"  video size: {0}\" -f (Format-Bytes $videoInfo.Length))");
            writer.WriteLine("Write-Host (\"  audio size: {0}\" -f (Format-Bytes $audioInfo.Length))");
            writer.WriteLine("$spaceCheckFailed = $false");
            writer.WriteLine("try {");
            writer.WriteLine("    $driveRoot = [System.IO.Path]::GetPathRoot($out)");
            writer.WriteLine("    $drive = New-Object System.IO.DriveInfo -ArgumentList $driveRoot");
            writer.WriteLine("    $estimatedAudioBytes = [Int64][Math]::Ceiling($expectedSeconds * 24000.0)");
            writer.WriteLine("    $requiredBytes = [Int64]($videoInfo.Length + $estimatedAudioBytes + 536870912)");
            writer.WriteLine("    Write-Host (\"  free space: {0}\" -f (Format-Bytes $drive.AvailableFreeSpace))");
            writer.WriteLine("    Write-Host (\"  required approx: {0}\" -f (Format-Bytes $requiredBytes))");
            writer.WriteLine("    if ($drive.AvailableFreeSpace -lt $requiredBytes) {");
            writer.WriteLine("        $spaceCheckFailed = $true");
            writer.WriteLine("        throw (\"Not enough free space for final mux. Free {0}, need approximately {1}.\" -f (Format-Bytes $drive.AvailableFreeSpace), (Format-Bytes $requiredBytes))");
            writer.WriteLine("    }");
            writer.WriteLine("} catch {");
            writer.WriteLine("    if ($spaceCheckFailed) { throw }");
            writer.WriteLine("    Write-Host (\"  free space check unavailable: {0}\" -f $_.Exception.Message)");
            writer.WriteLine("}");
            writer.WriteLine("# No +faststart by default: local final files do not need the extra full-file rewrite.");
            writer.WriteLine("try {");
            writer.WriteLine("    ffmpeg -n -nostdin -hide_banner -i $video -i $audio -map 0:v:0 -map 1:a:0 -c:v copy -c:a aac -b:a 192k -shortest $tmp");
            writer.WriteLine("    if ($LASTEXITCODE -ne 0) { throw \"ffmpeg final mux failed with exit code $LASTEXITCODE\" }");
            writer.WriteLine("    if (-not (Test-Path -LiteralPath $tmp)) { throw \"Missing temporary final MP4 output: $tmp\" }");
            writer.WriteLine("    if (Test-Path -LiteralPath $out) { [System.IO.File]::Replace($tmp, $out, [NullString]::Value) }");
            writer.WriteLine("    else { [System.IO.File]::Move($tmp, $out) }");
            writer.WriteLine("} finally {");
            writer.WriteLine("    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }");
            writer.WriteLine("}");
            writer.WriteLine("if (-not (Test-Path -LiteralPath $out)) { throw \"Missing final MP4 output: $out\" }");
            writer.WriteLine("Write-Host (\"Final output size: {0:n0} bytes\" -f (Get-Item -LiteralPath $out).Length)");
            writer.WriteLine("Write-Host " + PsQuote("Final output ready: ") + " $out");
        }
    }

    private void WritePowerShellUtf8Header(StreamWriter writer)
    {
        writer.WriteLine("$script:Utf8NoBom = New-Object System.Text.UTF8Encoding $false");
        writer.WriteLine("$OutputEncoding = $script:Utf8NoBom");
        writer.WriteLine("[Console]::OutputEncoding = $script:Utf8NoBom");
    }

    private List<PartInfo> BuildOrderedParts(List<SimpleSegment> simple, List<Range> complex, string vegasDir)
    {
        List<PartInfo> parts = new List<PartInfo>();
        for (int i = 0; i < simple.Count; i++)
            parts.Add(new PartInfo(simple[i].TimelineStart, simple[i].TimelineEnd, simple[i].OutFile));
        for (int i = 0; i < complex.Count; i++)
        {
            Range range = complex[i];
            string file = Path.Combine(vegasDir, String.Format(CultureInfo.InvariantCulture, "vegas_{0:000}_{1}_{2}.mp4", i, range.Start, range.End));
            parts.Add(new PartInfo(range.Start, range.End, file));
        }
        parts.Sort(delegate(PartInfo a, PartInfo b)
        {
            int c = a.Start.CompareTo(b.Start);
            if (c != 0) return c;
            return a.End.CompareTo(b.End);
        });
        return parts;
    }

    private void WriteSummary(string path, long startFrame, long endFrame, double fps, List<Range> complex, List<SimpleSegment> simple,
        string complexCsv, string simpleCsv, string vegasRanges, string projectRanges, string ffmpegScript, string assembleScript, string audioFile, string muxScript, string finalFile)
    {
        long complexFrames = 0;
        foreach (Range range in complex)
            complexFrames += range.End - range.Start;
        long simpleFrames = 0;
        foreach (SimpleSegment segment in simple)
            simpleFrames += segment.TimelineEnd - segment.TimelineStart;

        using (StreamWriter writer = new StreamWriter(path, false, Encoding.UTF8))
        {
            writer.WriteLine("selectionFrames=" + startFrame + "-" + endFrame);
            writer.WriteLine("selectionSeconds=" + Seconds(endFrame - startFrame, fps));
            writer.WriteLine("fps=" + Num(fps));
            writer.WriteLine("complexRanges=" + complex.Count.ToString(CultureInfo.InvariantCulture));
            writer.WriteLine("complexFrames=" + complexFrames.ToString(CultureInfo.InvariantCulture));
            writer.WriteLine("complexSeconds=" + Seconds(complexFrames, fps));
            writer.WriteLine("simpleSegments=" + simple.Count.ToString(CultureInfo.InvariantCulture));
            writer.WriteLine("simpleFrames=" + simpleFrames.ToString(CultureInfo.InvariantCulture));
            writer.WriteLine("simpleSeconds=" + Seconds(simpleFrames, fps));
            writer.WriteLine("validationExpectedSeconds=" + Seconds(endFrame - startFrame, fps));
            writer.WriteLine("validationToleranceSeconds=" + Num(DurationToleranceSeconds(fps)));
            writer.WriteLine();
            writer.WriteLine("complex detail:");
            foreach (Range range in complex)
            {
                writer.WriteLine("  " + range.Start.ToString(CultureInfo.InvariantCulture) + "-" +
                    range.End.ToString(CultureInfo.InvariantCulture) + " " +
                    String.Join("; ", range.Reasons.ToArray()));
            }
            writer.WriteLine();
            writer.WriteLine("files:");
            writer.WriteLine("  " + complexCsv);
            writer.WriteLine("  " + simpleCsv);
            writer.WriteLine("  " + vegasRanges);
            writer.WriteLine("  " + projectRanges);
            writer.WriteLine("  " + ffmpegScript);
            writer.WriteLine("  " + assembleScript);
            writer.WriteLine("  " + audioFile);
            writer.WriteLine("  " + muxScript);
            writer.WriteLine("  " + finalFile);
        }
    }

    private void ValidateFinalDuration(string finalFile, double expectedSeconds, double fps, TextBox log, string logPath)
    {
        if (!FileReady(finalFile))
            throw new ApplicationException("Final MP4 is missing or empty:\r\n" + finalFile);

        double tolerance = DurationToleranceSeconds(fps);
        AppendProgress(log, logPath, "Final duration check started.");
        AppendProgress(log, logPath, "  file: " + finalFile);
        AppendProgress(log, logPath, "  size: " + FormatFileSize(finalFile));
        AppendProgress(log, logPath, "  expected: " + Num(expectedSeconds) + " sec");
        AppendProgress(log, logPath, "  tolerance: +/- " + Num(tolerance) + " sec");

        double actualSeconds = ProbeDurationSeconds(finalFile);
        double diff = actualSeconds - expectedSeconds;
        AppendProgress(log, logPath, "  actual: " + Num(actualSeconds) + " sec");
        AppendProgress(log, logPath, "  diff: " + Num(diff) + " sec");

        if (Math.Abs(diff) > tolerance)
        {
            string direction = diff < 0.0 ? "shorter" : "longer";
            throw new ApplicationException(
                "Final MP4 duration mismatch. Expected " + Num(expectedSeconds) +
                " sec, got " + Num(actualSeconds) + " sec (" + direction +
                " by " + Num(Math.Abs(diff)) + " sec). Temporary folder was kept for inspection.");
        }

        AppendProgress(log, logPath, "Final duration check passed.");
    }

    private double DurationToleranceSeconds(double fps)
    {
        return Math.Max(0.35, 4.0 / fps);
    }

    private void VerifyFfprobeAvailable(TextBox log, string logPath)
    {
        ProcessStartInfo info = new ProcessStartInfo();
        info.FileName = "ffprobe";
        info.Arguments = "-version";
        info.UseShellExecute = false;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.CreateNoWindow = true;

        using (Process process = new Process())
        {
            process.StartInfo = info;
            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                throw new ApplicationException("ffprobe is required for duration checks but could not be started: " + ex.Message);
            }

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
                throw new ApplicationException("ffprobe is required for duration checks but failed to start correctly:\r\n" + error.Trim());

            string[] lines = output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string version = lines.Length > 0 ? lines[0].Trim() : "ffprobe available";
            AppendProgress(log, logPath, "Duration probe: " + version);
        }
    }

    private double ProbeDurationSeconds(string path)
    {
        if (!File.Exists(path))
            throw new ApplicationException("Cannot probe missing media file:\r\n" + path);

        ProcessStartInfo info = new ProcessStartInfo();
        info.FileName = "ffprobe";
        info.Arguments = "-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 " + CmdQuote(path);
        info.UseShellExecute = false;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.CreateNoWindow = true;

        using (Process process = new Process())
        {
            process.StartInfo = info;
            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                throw new ApplicationException("ffprobe is required for duration checks but could not be started: " + ex.Message);
            }

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
                throw new ApplicationException("ffprobe failed for:\r\n" + path + "\r\n\r\n" + error.Trim());

            string[] lines = output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                double seconds;
                if (Double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
                    return seconds;
            }
        }

        throw new ApplicationException("ffprobe did not return a duration for:\r\n" + path);
    }

    private string FormatFileSize(string path)
    {
        try
        {
            long bytes = new FileInfo(path).Length;
            double mb = ((double)bytes) / (1024.0 * 1024.0);
            return bytes.ToString("n0", CultureInfo.InvariantCulture) + " bytes (" + mb.ToString("0.00", CultureInfo.InvariantCulture) + " MB)";
        }
        catch
        {
            return "unknown size";
        }
    }

    private string Seconds(long frames, double fps)
    {
        return Num(((double)frames) / fps);
    }

    private string Num(double value)
    {
        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private string Csv(string value)
    {
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private string PsQuote(string value)
    {
        return "'" + value.Replace("'", "''") + "'";
    }

    private string CmdQuote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private string EscapeFfconcat(string value)
    {
        // The ffconcat path is embedded in a PowerShell double-quoted string.
        // Escape PowerShell interpolation after applying ffconcat quoting.
        return value.Replace("\\", "/").Replace("'", "'\\''")
            .Replace("`", "``").Replace("$", "`$").Replace("\"", "`\"");
    }

    private class VideoEventInfo
    {
        public VideoTrack Track;
        public VideoEvent Event;
        public long StartFrame;
        public long EndFrame;

        public VideoEventInfo(VideoTrack track, VideoEvent ev)
        {
            Track = track;
            Event = ev;
            StartFrame = ev.Start.FrameCount;
            EndFrame = ev.End.FrameCount;
        }
    }

    private class Range
    {
        public long Start;
        public long End;
        public List<string> Reasons = new List<string>();

        public Range(long start, long end, string reason)
        {
            Start = start;
            End = end;
            Reasons.Add(reason);
        }

        public Range Clone()
        {
            Range copy = new Range(Start, End, "");
            copy.Reasons.Clear();
            foreach (string reason in Reasons)
                copy.Reasons.Add(reason);
            return copy;
        }
    }

    private class SimpleSegment
    {
        public int Index;
        public long TimelineStart;
        public long TimelineEnd;
        public long SourceStart;
        public long SourceEnd;
        public string SourceFile;
        public string OutFile;

        public SimpleSegment(int index, long timelineStart, long timelineEnd, long sourceStart, long sourceEnd, string sourceFile, string outFile)
        {
            Index = index;
            TimelineStart = timelineStart;
            TimelineEnd = timelineEnd;
            SourceStart = sourceStart;
            SourceEnd = sourceEnd;
            SourceFile = sourceFile;
            OutFile = outFile;
        }
    }

    private class PartInfo
    {
        public long Start;
        public long End;
        public string FilePath;

        public PartInfo(long start, long end, string filePath)
        {
            Start = start;
            End = end;
            FilePath = filePath;
        }
    }

    private class FragmentProgress
    {
        public Label Label;
        public int Done;
        public int Total;

        public FragmentProgress(Label label, int total)
        {
            Label = label;
            Done = 0;
            Total = total;
        }
    }

    private class RunSelection
    {
        public string TempDirectory;
        public string OutputDirectory;
        public RenderTemplate VideoTemplate;
        public RenderTemplate AudioTemplate;
        public string VideoTemplateKey;
        public string AudioTemplateKey;
        public string VideoTemplateDisplay;
        public string AudioTemplateDisplay;
        public int StartStepIndex;

        public RunSelection(string tempDirectory, string outputDirectory, RenderChoice videoChoice, RenderChoice audioChoice, int startStepIndex)
        {
            TempDirectory = tempDirectory;
            OutputDirectory = outputDirectory;
            VideoTemplate = videoChoice.Template;
            AudioTemplate = audioChoice.Template;
            VideoTemplateKey = videoChoice.PersistentKey();
            AudioTemplateKey = audioChoice.PersistentKey();
            VideoTemplateDisplay = videoChoice.ToString();
            AudioTemplateDisplay = audioChoice.ToString();
            StartStepIndex = startStepIndex;
        }
    }

    private class StageChoice
    {
        public int StepIndex;
        public string Display;

        public StageChoice(int stepIndex, string display)
        {
            StepIndex = stepIndex;
            Display = display;
        }

        public override string ToString()
        {
            return Display;
        }
    }

    private delegate bool OutputProbe();

    private delegate void StepAction();

    private enum StepStatus
    {
        Pending,
        Running,
        Done,
        Failed
    }

    private class PipelineStep
    {
        public string Name;
        public string Detail;
        public OutputProbe IsComplete;
        public StepStatus Status;
        public Panel Box;
        public Label StatusLabel;
        public bool AlwaysRun;

        public PipelineStep(string name, string detail, OutputProbe isComplete)
            : this(name, detail, isComplete, false)
        {
        }

        public PipelineStep(string name, string detail, OutputProbe isComplete, bool alwaysRun)
        {
            Name = name;
            Detail = detail;
            IsComplete = isComplete;
            Status = StepStatus.Pending;
            AlwaysRun = alwaysRun;
        }
    }

    private class RenderChoice
    {
        public Renderer Renderer;
        public RenderTemplate Template;

        public RenderChoice(Renderer renderer, RenderTemplate template)
        {
            Renderer = renderer;
            Template = template;
        }

        public string PersistentKey()
        {
            return SafeText(Renderer.Name) +
                "|" + SafeText(Renderer.FileTypeName) +
                "|" + SafeText(Renderer.FileExtension) +
                "|" + SafeText(Template.Name);
        }

        public override string ToString()
        {
            return SafeText(Renderer.Name) +
                " | " + SafeText(Renderer.FileTypeName) +
                " | " + SafeText(Template.Name) +
                " | " + Template.VideoWidth.ToString(CultureInfo.InvariantCulture) + "x" +
                Template.VideoHeight.ToString(CultureInfo.InvariantCulture) +
                " | " + Template.VideoFrameRate.ToString("0.###", CultureInfo.InvariantCulture) + " fps" +
                " | ." + SafeText(Renderer.FileExtension);
        }

        private string SafeText(string value)
        {
            return value == null ? "" : value;
        }
    }
}
