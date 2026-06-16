using System;
using Xunit;
using NAudio.Wave;
using MultiAudioRouter;

namespace MultiAudioRouter.Tests
{
    public class TestSampleProvider : ISampleProvider
    {
        private readonly float[] samples;
        private int position;

        public TestSampleProvider(WaveFormat format, float[] samples)
        {
            WaveFormat = format;
            this.samples = samples;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesToCopy = Math.Min(count, samples.Length - position);
            if (samplesToCopy <= 0) return 0;
            Array.Copy(samples, position, buffer, offset, samplesToCopy);
            position += samplesToCopy;
            return samplesToCopy;
        }

        public void Reset()
        {
            position = 0;
        }
    }

    public class CrossoverFilterProviderTests
    {
        private float[] GenerateSineWave(int sampleRate, int channels, double frequency, double durationSeconds)
        {
            int monoSamples = (int)(sampleRate * durationSeconds);
            float[] samples = new float[monoSamples * channels];
            for (int i = 0; i < monoSamples; i++)
            {
                float val = (float)Math.Sin(2 * Math.PI * frequency * i / sampleRate);
                for (int ch = 0; ch < channels; ch++)
                {
                    samples[i * channels + ch] = val;
                }
            }
            return samples;
        }

        private float CalculateRms(float[] buffer, int offset, int count)
        {
            double sum = 0;
            for (int i = 0; i < count; i++)
            {
                sum += buffer[offset + i] * buffer[offset + i];
            }
            return (float)Math.Sqrt(sum / count);
        }

        [Fact]
        public void Constructor_Initialization_PropertiesMatchSource()
        {
            // Arrange
            var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
            var source = new TestSampleProvider(format, new float[100]);

            // Act
            var crossover = new MainWindow.UnifiedDspProvider(source, CrossoverMode.FullRange, 80f, 0);

            // Assert
            Assert.Equal(format, crossover.WaveFormat);
        }

        [Fact]
        public void FullRangeMode_DoesNotModifySamples()
        {
            // Arrange
            int sampleRate = 48000;
            int channels = 2;
            var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            
            // Create random samples
            var random = new Random(42);
            float[] inputSamples = new float[1000];
            for (int i = 0; i < inputSamples.Length; i++)
            {
                inputSamples[i] = (float)(random.NextDouble() * 2.0 - 1.0);
            }
            
            var source = new TestSampleProvider(format, inputSamples);
            var crossover = new MainWindow.UnifiedDspProvider(source, CrossoverMode.FullRange, 80f, 0);
            
            float[] outputBuffer = new float[1000];

            // Act
            int read = crossover.Read(outputBuffer, 0, outputBuffer.Length);

            // Assert
            Assert.Equal(inputSamples.Length, read);
            for (int i = 0; i < read; i++)
            {
                Assert.Equal(inputSamples[i], outputBuffer[i]);
            }
        }

        [Theory]
        [InlineData(2)] // Stereo
        public void LowPassMode_AttenuatesHighFrequencies_PassesLowFrequencies(int channels)
        {
            // Arrange
            int sampleRate = 48000;
            float crossoverFrequency = 80f;
            var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

            // 1. Generate 40 Hz sine wave (below 80 Hz) - 0.2 seconds
            float[] lowFreqSamples = GenerateSineWave(sampleRate, channels, 40.0, 0.2);
            var lowFreqSource = new TestSampleProvider(format, lowFreqSamples);
            var lowFreqCrossover = new MainWindow.UnifiedDspProvider(lowFreqSource, CrossoverMode.LowPass, crossoverFrequency, 0);
            float[] lowFreqOutput = new float[lowFreqSamples.Length];

            // 2. Generate 1000 Hz sine wave (above 80 Hz) - 0.2 seconds
            float[] highFreqSamples = GenerateSineWave(sampleRate, channels, 1000.0, 0.2);
            var highFreqSource = new TestSampleProvider(format, highFreqSamples);
            var highFreqCrossover = new MainWindow.UnifiedDspProvider(highFreqSource, CrossoverMode.LowPass, crossoverFrequency, 0);
            float[] highFreqOutput = new float[highFreqSamples.Length];

            // Act
            int lowFreqRead = lowFreqCrossover.Read(lowFreqOutput, 0, lowFreqOutput.Length);
            int highFreqRead = highFreqCrossover.Read(highFreqOutput, 0, highFreqOutput.Length);

            // Assert
            Assert.Equal(lowFreqSamples.Length, lowFreqRead);
            Assert.Equal(highFreqSamples.Length, highFreqRead);

            // Discard the initial transient response (first half) to measure steady-state RMS
            int halfLength = lowFreqSamples.Length / 2;
            
            float inputLowRms = CalculateRms(lowFreqSamples, halfLength, halfLength);
            float outputLowRms = CalculateRms(lowFreqOutput, halfLength, halfLength);
            
            float inputHighRms = CalculateRms(highFreqSamples, halfLength, halfLength);
            float outputHighRms = CalculateRms(highFreqOutput, halfLength, halfLength);

            float lowPassGain = outputLowRms / inputLowRms;
            float highPassGain = outputHighRms / inputHighRms;

            // Low frequency (40 Hz) should pass through with minimal attenuation
            Assert.True(lowPassGain > 0.80f, $"Low frequency gain was {lowPassGain:F4}, expected > 0.80");
            
            // High frequency (1000 Hz) should be significantly attenuated
            Assert.True(highPassGain < 0.05f, $"High frequency gain was {highPassGain:F4}, expected < 0.05");
        }

        [Theory]
        [InlineData(2)] // Stereo
        public void HighPassMode_AttenuatesLowFrequencies_PassesHighFrequencies(int channels)
        {
            // Arrange
            int sampleRate = 48000;
            float crossoverFrequency = 80f;
            var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

            // 1. Generate 40 Hz sine wave (below 80 Hz) - 0.2 seconds
            float[] lowFreqSamples = GenerateSineWave(sampleRate, channels, 40.0, 0.2);
            var lowFreqSource = new TestSampleProvider(format, lowFreqSamples);
            var lowFreqCrossover = new MainWindow.UnifiedDspProvider(lowFreqSource, CrossoverMode.HighPass, crossoverFrequency, 0);
            float[] lowFreqOutput = new float[lowFreqSamples.Length];

            // 2. Generate 1000 Hz sine wave (above 80 Hz) - 0.2 seconds
            float[] highFreqSamples = GenerateSineWave(sampleRate, channels, 1000.0, 0.2);
            var highFreqSource = new TestSampleProvider(format, highFreqSamples);
            var highFreqCrossover = new MainWindow.UnifiedDspProvider(highFreqSource, CrossoverMode.HighPass, crossoverFrequency, 0);
            float[] highFreqOutput = new float[highFreqSamples.Length];

            // Act
            int lowFreqRead = lowFreqCrossover.Read(lowFreqOutput, 0, lowFreqOutput.Length);
            int highFreqRead = highFreqCrossover.Read(highFreqOutput, 0, highFreqOutput.Length);

            // Assert
            Assert.Equal(lowFreqSamples.Length, lowFreqRead);
            Assert.Equal(highFreqSamples.Length, highFreqRead);

            // Discard the initial transient response (first half) to measure steady-state RMS
            int halfLength = lowFreqSamples.Length / 2;
            
            float inputLowRms = CalculateRms(lowFreqSamples, halfLength, halfLength);
            float outputLowRms = CalculateRms(lowFreqOutput, halfLength, halfLength);
            
            float inputHighRms = CalculateRms(highFreqSamples, halfLength, halfLength);
            float outputHighRms = CalculateRms(highFreqOutput, halfLength, halfLength);

            float lowPassGain = outputLowRms / inputLowRms;
            float highPassGain = outputHighRms / inputHighRms;

            // Low frequency (40 Hz) should be significantly attenuated
            Assert.True(lowPassGain < 0.30f, $"Low frequency gain was {lowPassGain:F4}, expected < 0.30");
            
            // High frequency (1000 Hz) should pass through with minimal attenuation
            Assert.True(highPassGain > 0.80f, $"High frequency gain was {highPassGain:F4}, expected > 0.80");
        }

        [Fact]
        public void DynamicTransition_AppliesFilterCoefficientsOnNextRead()
        {
            // Arrange
            int sampleRate = 48000;
            int channels = 2;
            var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

            // Generate high-frequency sine wave (1000 Hz) - 0.4 seconds total
            float[] inputSamples = GenerateSineWave(sampleRate, channels, 1000.0, 0.4);
            var source = new TestSampleProvider(format, inputSamples);

            // Start in FullRange mode
            var crossover = new MainWindow.UnifiedDspProvider(source, CrossoverMode.FullRange, 80f, 0);

            int halfLength = inputSamples.Length / 2;
            float[] firstHalfOutput = new float[halfLength];
            float[] secondHalfOutput = new float[halfLength];

            // Act
            // 1. Read first half in FullRange mode
            int read1 = crossover.Read(firstHalfOutput, 0, halfLength);

            // Verify first half matches input exactly (FullRange)
            for (int i = 0; i < read1; i++)
            {
                Assert.Equal(inputSamples[i], firstHalfOutput[i]);
            }

            // 2. Dynamically change to LowPass (should filter out 1000 Hz)
            crossover.SetCrossover(CrossoverMode.LowPass, 80f);

            // 3. Read second half (should apply filtering on next read)
            int read2 = crossover.Read(secondHalfOutput, 0, halfLength);

            // Assert
            Assert.Equal(halfLength, read1);
            Assert.Equal(halfLength, read2);

            // Measure RMS of the second half after transient settling
            int settlingLength = halfLength / 2;
            float inputSecondHalfRms = CalculateRms(inputSamples, halfLength + settlingLength, settlingLength);
            float outputSecondHalfRms = CalculateRms(secondHalfOutput, settlingLength, settlingLength);

            float lowPassGain = outputSecondHalfRms / inputSecondHalfRms;

            // In LowPass mode at 80Hz, the 1000Hz signal should be heavily attenuated
            Assert.True(lowPassGain < 0.05f, $"Dynamic transition low-pass gain was {lowPassGain:F4}, expected < 0.05");
        }

        [Fact]
        public void UnifiedDspProvider_AppliesQueueDelay()
        {
            // Arrange
            int sampleRate = 48000;
            int channels = 2;
            var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            
            // 10 ms delay = 480 samples per channel * 2 channels = 960 samples
            double delayMs = 10.0;
            int expectedDelaySamples = 960;

            float[] inputSamples = new float[1200];
            for (int i = 0; i < inputSamples.Length; i++)
            {
                inputSamples[i] = 1.0f; // constant non-zero signal
            }
            
            var source = new TestSampleProvider(format, inputSamples);
            var dsp = new MainWindow.UnifiedDspProvider(source, CrossoverMode.FullRange, 80f, delayMs);
            
            float[] outputBuffer = new float[1200];

            // Act
            int read = dsp.Read(outputBuffer, 0, outputBuffer.Length);

            // Assert
            Assert.Equal(inputSamples.Length, read);

            // The first expectedDelaySamples should be 0.0f (silence)
            for (int i = 0; i < expectedDelaySamples; i++)
            {
                Assert.Equal(0.0f, outputBuffer[i]);
            }

            // The samples after the delay should match the input samples (1.0f)
            for (int i = expectedDelaySamples; i < read; i++)
            {
                Assert.Equal(1.0f, outputBuffer[i]);
            }
        }

        [Fact]
        public void UnifiedDspProvider_ShrinkingDelay_DropsDelayInstantly()
        {
            // Arrange
            int sampleRate = 48000;
            int channels = 2;
            var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            
            // Start with 50 ms delay = 4800 samples
            double initialDelayMs = 50.0;
            float[] inputSamples = new float[6000];
            for (int i = 0; i < inputSamples.Length; i++)
            {
                inputSamples[i] = (float)i; // ramp values to trace samples
            }
            
            var source = new TestSampleProvider(format, inputSamples);
            var dsp = new MainWindow.UnifiedDspProvider(source, CrossoverMode.FullRange, 80f, initialDelayMs);
            
            float[] outputBuffer1 = new float[4800];
            
            // Act 1: Read 4800 samples (which fills the queue with initial delay of 4800 samples, and outputs 0s)
            dsp.Read(outputBuffer1, 0, 4800);
            
            // Verify initial delay output is all silence
            for (int i = 0; i < 4800; i++)
            {
                Assert.Equal(0.0f, outputBuffer1[i]);
            }

            // Act 2: Reduce delay to 10 ms = 960 samples
            dsp.DelayMs = 10.0;
            
            float[] outputBuffer2 = new float[1200];
            dsp.Read(outputBuffer2, 0, 1200);

            // Assert: The excess samples in the queue should be discarded.
            // When we reduced delay to 10ms, the queue had 4800 samples.
            // Upon the next Read, the queue gets drained to 962 samples (960 target + 2 for the first pair).
            // Then we dequeue from the queue. The oldest sample remaining in the queue should be enqueued at sample 4800 - 960 = 3840.
            // Verify that the output starts with value 3840.
            Assert.Equal(3840f, outputBuffer2[0]);
            Assert.Equal(3841f, outputBuffer2[1]);
        }
    }
}
