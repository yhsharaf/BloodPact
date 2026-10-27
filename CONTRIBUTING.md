# Contributing to Blood Pact

Thanks for helping! Bug reports and pull requests are both welcome.

## Reporting a bug

Open an issue and include:

- what happened, and what you expected;
- the Blood Pact version you both run (shown in the top-left corner of the game);
- whether you were the host or the guest, and Steam or Tailscale;
- the bug report folder: press **F10** right after the bug (either player). It saves both players' logs in
  `BepInEx\BloodPact-reports`; zip that folder and attach it.

## Sending a change

1. Fork the repository and make your change on a branch.
2. Build it (see "Building from source" in the README) and try it in the game.
3. If your change touches co-op behaviour, run the self-test for the area you changed (for example
   `-Scenario monsters`, `-Scenario bridges`, or the boss scenarios) and mention the results.
4. Open a pull request saying what it fixes or adds and how you tested it.

Every pull request is reviewed before it is merged. Keep changes focused: one fix or feature per pull request
makes that much easier.

## A few rules of the code

- Never patch (Harmony) a game method that takes a value type (struct) argument: with IL2CPP that crashes
  the game. Calling such methods is fine.
- The other player's game is a separate world: anything shared is sent as a message and applied on arrival.
  Keep both games deciding the same thing the same way, and never echo a change back to where it came from.
- Comments explain *why* in plain words; follow the style of the surrounding code.

By contributing, you agree that your contribution is released under the project's [MIT License](LICENSE).
