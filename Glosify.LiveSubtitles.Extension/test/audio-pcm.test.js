import test from "node:test";
import assert from "node:assert/strict";
import {
  StreamingPcm16Downsampler,
  accumulateSkippedAudioMilliseconds,
  pcm16ToBase64,
} from "../lib/audio-pcm.js";

test("downsamples continuous 48 kHz blocks to 24 kHz PCM16", () => {
  const downsampler = new StreamingPcm16Downsampler(48_000);
  const first = downsampler.process(Float32Array.from([1, 1, -1, -1]));
  const second = downsampler.process(Float32Array.from([0.5, 0.5]));

  assert.deepEqual([...first], [32767, -32768]);
  assert.deepEqual([...second], [16384]);
});

test("preserves sample count across non-integer block ratios", () => {
  const downsampler = new StreamingPcm16Downsampler(44_100);
  let outputSamples = 0;
  for (let block = 0; block < 10; block += 1) {
    outputSamples += downsampler.process(new Float32Array(4_410)).length;
  }
  assert.equal(outputSamples, 24_000);
});

test("encodes signed PCM samples as little-endian base64", () => {
  assert.equal(pcm16ToBase64(Int16Array.from([1, -2])), "AQD+/w==");
});

test("sustained 48 kHz backpressure reaches the stop threshold after two seconds", () => {
  let skippedMs = 0;
  for (let block = 0; block < 23; block += 1) {
    skippedMs = accumulateSkippedAudioMilliseconds(skippedMs, 4096, 48_000);
  }
  assert.ok(skippedMs < 2_000);

  skippedMs = accumulateSkippedAudioMilliseconds(skippedMs, 4096, 48_000);
  assert.ok(skippedMs >= 2_000);
});
