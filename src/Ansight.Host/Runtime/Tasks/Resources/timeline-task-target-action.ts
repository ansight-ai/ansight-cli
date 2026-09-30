
  // Step $SEQUENCE$: $DESCRIPTION$
  const step$SEQUENCE$Ready = await ansight.ui.waitFor({
$SELECTOR_PROPERTIES$    condition: "visible",
    timeoutMs: 10000
  });
  expect(step$SEQUENCE$Ready.satisfied, {
    id: "step-$SEQUENCE$-target-visible"
  }).toBe(true);
$OPERATION$
  expect(step$SEQUENCE$.performed, {
    id: "step-$SEQUENCE$-performed",
    message: $PERFORMED_MESSAGE$
  }).toBe(true);
