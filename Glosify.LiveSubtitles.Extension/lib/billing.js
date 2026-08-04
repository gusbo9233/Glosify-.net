export const MINUTE_MS = 60_000;

export function getBillingAction({
  elapsedMs,
  currentMinute,
  nextMinuteReserved,
  stopAtBoundary,
  maxSessionMinutes,
  renewalLeadSeconds,
}) {
  const boundaryMs = currentMinute * MINUTE_MS;
  if (currentMinute >= maxSessionMinutes) {
    return elapsedMs >= boundaryMs
      ? { type: "reconnect" }
      : { type: "none" };
  }
  if (elapsedMs >= boundaryMs) {
    if (stopAtBoundary || !nextMinuteReserved) {
      return { type: "stop" };
    }
    return { type: "begin", minuteIndex: currentMinute + 1 };
  }

  const reserveAt = boundaryMs - renewalLeadSeconds * 1000;
  if (!nextMinuteReserved && !stopAtBoundary && elapsedMs >= reserveAt) {
    return { type: "reserve", minuteIndex: currentMinute + 1 };
  }
  return { type: "none" };
}
