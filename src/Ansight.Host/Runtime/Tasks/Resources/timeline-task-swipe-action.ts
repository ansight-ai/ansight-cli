
  // Step $SEQUENCE$: $DESCRIPTION$
  const step$SEQUENCE$ = await ansight.ui.swipe({
    orientation: $ORIENTATION$,
    length: $LENGTH$,
    durationMs: $DURATION_MILLISECONDS$
  });
  expect(step$SEQUENCE$.performed, {
    id: "step-$SEQUENCE$-performed",
    message: $PERFORMED_MESSAGE$
  }).toBe(true);
