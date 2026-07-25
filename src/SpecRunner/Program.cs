using SpecRunner.Cli;

// The tick binary is installed once and invoked per-instance with a subcommand
// and its config path (contracts/cli-commands.md). Command bodies land in Phase 2+;
// this dispatch skeleton exists so Phase 1 compiles and the exit-code contract is fixed.
return CommandDispatcher.Run(args);
