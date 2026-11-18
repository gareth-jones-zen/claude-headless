// claude-headless-background.exe: no window, ever. What the run-queue task runs, and what queued jobs can run.
return ClaudeHeadlessTool.Cli.Run(args, new ClaudeHeadlessTool.CliHost { Out = TextWriter.Null, Err = TextWriter.Null });
