using SecondKey.Cli;

return await SecondKeyCli.RunAsync(args, Console.Out, Console.Error, CancellationToken.None).ConfigureAwait(false);
