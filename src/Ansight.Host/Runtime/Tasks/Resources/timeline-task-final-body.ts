
  const finalState = await ansight.ui.waitFor({
    condition: "stable",
    timeoutMs: 10000,
    stableSamples: 3
  });
  expect(finalState.satisfied, {
    id: "workflow-stable",
    message: "The live UI should settle after the recorded workflow."
  }).toBe(true);
