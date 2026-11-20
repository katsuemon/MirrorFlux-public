"""Compare attack envelopes, not sample-exact audio, using Python's standard library.

Usage: python compare_mdx_timing.py reference.wav rendered.wav
Positive offset means the reference starts later. Excludes the first 5 seconds
to avoid the PCM present at the start of the supplied FM reference recording.
"""
import array
import math
import sys
import wave


def attacks(path):
    with wave.open(path, 'rb') as source:
        if source.getsampwidth() != 2 or source.getnchannels() != 2:
            raise ValueError('Expected stereo PCM16 WAV')
        rate = source.getframerate()
        samples = array.array('h', source.readframes(source.getnframes()))
        if sys.byteorder != 'little':
            samples.byteswap()
    rms = []
    for frame in range(int(len(samples) / 2 / rate * 200)):
        a, b = int(frame * rate / 200) * 2, int((frame + 1) * rate / 200) * 2
        rms.append(math.sqrt(sum(samples[i] ** 2 for i in range(a, b)) / (b - a)))
    return [max(0, rms[i] - rms[i - 2]) for i in range(2, len(rms))]


def compare(reference, rendered):
    def correlation(a, b, lag):
        a, b = max(a, -lag), min(b, len(rendered), len(reference) - lag)
        xy = sum(rendered[i] * reference[i + lag] for i in range(a, b))
        xx = sum(rendered[i] ** 2 for i in range(a, b))
        yy = sum(reference[i + lag] ** 2 for i in range(a, b))
        return xy / math.sqrt(xx * yy) if xx * yy else 0

    score, offset = max((correlation(2000, 6000, lag), lag) for lag in range(-400, 2001))
    print(f'Initial alignment: {offset / 200:.3f}s, correlation={score:.4f}')
    print('start,end,correlation,reference_offset_seconds')
    end = int(min(len(rendered), len(reference) - offset) / 200)
    for start in range(5, end - 4, 10):
        stop = min(start + 10, end)
        score, lag = max((correlation(start * 200, stop * 200, lag), lag)
                         for lag in range(offset - 40, offset + 41))
        print(f'{start},{stop},{score:.4f},{lag / 200:.3f}')
    # Match prominent positive-energy peaks within 20ms, one-to-one. This
    # measures acoustic attacks; soft/overlapping notes are not certified.
    def peaks(values):
        active = sorted(v for v in values[1000:] if v > 0)
        threshold = active[int(len(active) * .75)] if active else 0
        return [i for i in range(1000, min(len(values) - 2, end * 200))
                if values[i] > threshold and values[i] == max(values[i-2:i+3])]
    rp, pp = peaks(reference), peaks(rendered)
    available = set(rp)
    errors = []
    for i in sorted(pp, key=lambda i: rendered[i], reverse=True):
        candidates = [j for j in range(i + offset - 4, i + offset + 5) if j in available]
        if candidates:
            j = min(candidates, key=lambda j: abs(j-i-offset))
            available.remove(j)
            errors.append(abs(j-i-offset)*5)
    print(f'Prominent attacks: reference={len(rp)}, rendered={len(pp)}, matched_within_20ms={len(errors)}')
    if errors:
        print(f'Matched median error={sorted(errors)[len(errors)//2]}ms')
    print('Envelope agreement does not certify every note or identical timbre.')


if __name__ == '__main__':
    compare(attacks(sys.argv[1]), attacks(sys.argv[2]))
