export class StreamingPcm16Downsampler {
  constructor(inputRate, outputRate = 24_000) {
    if (!Number.isFinite(inputRate)
        || !Number.isFinite(outputRate)
        || inputRate < outputRate
        || outputRate <= 0) {
      throw new Error("The captured tab uses an unsupported audio sample rate.");
    }
    this.ratio = inputRate / outputRate;
    this.phase = 0;
    this.sum = 0;
  }

  process(input) {
    const output = [];
    for (let index = 0; index < input.length; index += 1) {
      const sample = Math.max(-1, Math.min(1, input[index]));
      let remaining = 1;
      while (remaining > 1e-9) {
        const take = Math.min(remaining, this.ratio - this.phase);
        this.sum += sample * take;
        this.phase += take;
        remaining -= take;
        if (this.phase >= this.ratio - 1e-9) {
          output.push(toPcm16(this.sum / this.ratio));
          this.phase = 0;
          this.sum = 0;
        }
      }
    }
    return Int16Array.from(output);
  }
}

export function pcm16ToBase64(samples) {
  const bytes = new Uint8Array(samples.length * 2);
  const view = new DataView(bytes.buffer);
  for (let index = 0; index < samples.length; index += 1) {
    view.setInt16(index * 2, samples[index], true);
  }

  let binary = "";
  const blockSize = 0x8000;
  for (let offset = 0; offset < bytes.length; offset += blockSize) {
    binary += String.fromCharCode(...bytes.subarray(offset, offset + blockSize));
  }
  return btoa(binary);
}

function toPcm16(sample) {
  return sample < 0
    ? Math.round(sample * 0x8000)
    : Math.round(sample * 0x7fff);
}
