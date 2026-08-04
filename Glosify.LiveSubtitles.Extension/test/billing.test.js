import test from "node:test";
import assert from "node:assert/strict";
import { getBillingAction } from "../lib/billing.js";

const defaults = {
  currentMinute: 1,
  nextMinuteReserved: false,
  stopAtBoundary: false,
  maxSessionMinutes: 30,
  renewalLeadSeconds: 5,
};

test("reserves the next minute five seconds before its boundary", () => {
  assert.deepEqual(getBillingAction({ ...defaults, elapsedMs: 55_000 }), {
    type: "reserve",
    minuteIndex: 2,
  });
});

test("begins a reserved minute at the boundary", () => {
  assert.deepEqual(getBillingAction({
    ...defaults,
    elapsedMs: 60_000,
    nextMinuteReserved: true,
  }), { type: "begin", minuteIndex: 2 });
});

test("stops before entering an unreserved minute", () => {
  assert.deepEqual(getBillingAction({ ...defaults, elapsedMs: 60_000 }), { type: "stop" });
});

test("reconnects instead of attempting minute 31", () => {
  assert.deepEqual(getBillingAction({
    ...defaults,
    elapsedMs: 30 * 60_000 - 5_000,
    currentMinute: 30,
  }), { type: "none" });
  assert.deepEqual(getBillingAction({
    ...defaults,
    elapsedMs: 30 * 60_000,
    currentMinute: 30,
  }), { type: "reconnect" });
});
