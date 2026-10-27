# Pending: bottom-right cards (not in the stable build)

`SignalCards.cs.pending` adds small notification cards next to Capri's spoken alert.
Kept out of `app/` because Windows Smart App Control on the laptop blocks new unsigned builds.

To bring it back (on a PC where new builds run, or once Capri is signed):
1. Copy it to `app/SignalCards.cs`.
2. In `app/Signal.cs` (SignalHub): add a `fresh` list filled in `Deliver` and a `TakeFresh()` method.
3. In `app/Capri.cs`: a `SignalCards agentCards` field, create it next to `agentSignal`,
   call `agentCards.Show(...)` for each `TakeFresh()` item and `agentCards.Tick()` in `CheckAgentSignal()`, dispose it in `Dispose`.
