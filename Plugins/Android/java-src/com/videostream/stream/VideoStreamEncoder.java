package com.videostream.stream;

import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.os.Bundle;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.nio.ByteBuffer;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.BlockingQueue;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

public class VideoStreamEncoder {
    private static final int COLOR_NV12 =
            MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420SemiPlanar;
    private static final byte[] START_CODE_4 = {0, 0, 0, 1};
    private static final byte[] START_CODE_3 = {0, 0, 1};

    private final AtomicBoolean running = new AtomicBoolean(false);

    private VideoStreamCallback callback;
    private MediaCodec codec;
    private String mime;
    private volatile boolean flipY = true;
    private volatile boolean forceKeyFrame = false;
    private BlockingQueue<Frame> pending;
    private Thread worker;
    private byte[] csd;

    public void setCallback(VideoStreamCallback cb) {
        this.callback = cb;
    }

    public void setFlipY(boolean flipY) {
        this.flipY = flipY;
    }

    public boolean open(int width, int height, int bitrate, int frameRate,
                        int iFrameIntervalSeconds, String mimeType, int maxQueuedFrames) {
        if (running.get()) return true;

        width &= ~1;
        height &= ~1;
        if (width <= 0 || height <= 0 || bitrate <= 0 || frameRate <= 0) {
            error("Invalid encoder parameters");
            return false;
        }

        try {
            if (maxQueuedFrames < 1) maxQueuedFrames = 1;
            pending = new ArrayBlockingQueue<Frame>(maxQueuedFrames);
            mime = mimeType;

            MediaFormat format = MediaFormat.createVideoFormat(mimeType, width, height);
            format.setInteger(MediaFormat.KEY_COLOR_FORMAT, COLOR_NV12);
            format.setInteger(MediaFormat.KEY_BIT_RATE, bitrate);
            format.setInteger(MediaFormat.KEY_FRAME_RATE, frameRate);
            format.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, iFrameIntervalSeconds);
            format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1);

            codec = MediaCodec.createEncoderByType(mimeType);
            codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
            codec.start();

            running.set(true);
            worker = new Thread(new Runnable() {
                @Override
                public void run() {
                    workerLoop();
                }
            }, "VideoStreamEncoder");
            worker.start();
            return true;
        } catch (Exception e) {
            error("open failed: " + e.getMessage());
            close();
            return false;
        }
    }

    public boolean pushFrame(byte[] rgba, int width, int height, long ptsUs) {
        if (!running.get()) return false;
        if (rgba == null || rgba.length < width * height * 4) return false;
        return pending.offer(new Frame(rgba, width, height, ptsUs));
    }

    public void requestKeyFrame() {
        forceKeyFrame = true;
        if (codec != null && running.get()) {
            try {
                Bundle params = new Bundle();
                params.putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0);
                codec.setParameters(params);
            } catch (Exception ignored) {
            }
        }
    }

    public void close() {
        running.set(false);

        if (worker != null) {
            worker.interrupt();
            try {
                worker.join(500);
            } catch (InterruptedException ignored) {
                Thread.currentThread().interrupt();
            }
            worker = null;
        }

        if (pending != null) pending.clear();
        if (codec != null) {
            try {
                codec.stop();
            } catch (Exception ignored) {
            }
            try {
                codec.release();
            } catch (Exception ignored) {
            }
            codec = null;
        }
        csd = null;
    }

    public boolean isRunning() {
        return running.get();
    }

    private void workerLoop() {
        try {
            while (running.get()) {
                Frame frame;
                try {
                    frame = pending.poll(20, TimeUnit.MILLISECONDS);
                } catch (InterruptedException e) {
                    break;
                }

                if (frame == null) {
                    drainOutput(false);
                    continue;
                }

                pumpFrame(frame);
                drainOutput(true);
            }
        } catch (Exception e) {
            error("encoder worker failed: " + e.getMessage());
        } finally {
            releaseCodec();
        }
    }

    private void pumpFrame(Frame frame) {
        try {
            int inputIndex = codec.dequeueInputBuffer(10_000);
            if (inputIndex < 0) return;

            byte[] nv12 = toNv12(frame.rgba, frame.width, frame.height, flipY);
            ByteBuffer input = codec.getInputBuffer(inputIndex);
            input.clear();
            input.put(nv12);
            codec.queueInputBuffer(inputIndex, 0, nv12.length, frame.ptsUs, 0);
        } catch (Exception e) {
            error("pumpFrame failed: " + e.getMessage());
        }
    }

    private void drainOutput(boolean drainAll) {
        MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();

        while (running.get()) {
            int outputIndex = codec.dequeueOutputBuffer(info, drainAll ? 0 : 0);
            if (outputIndex == MediaCodec.INFO_TRY_AGAIN_LATER) {
                break;
            }
            if (outputIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                continue;
            }
            if (outputIndex < 0) continue;

            try {
                ByteBuffer output = codec.getOutputBuffer(outputIndex);
                byte[] encoded = new byte[info.size];
                output.position(info.offset);
                output.get(encoded, info.offset, info.size);
                encoded = ensureAnnexB(encoded);

                boolean config = (info.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0;
                boolean keyFrame = (info.flags & MediaCodec.BUFFER_FLAG_SYNC_FRAME) != 0;

                if (config) {
                    if (encoded.length > 0) {
                        csd = encoded;
                        fire(encoded, info.presentationTimeUs, true, false);
                    }
                } else {
                    byte[] effective = encoded;
                    if (keyFrame) {
                        if (csd == null || csd.length == 0) {
                            byte[] extracted = extractParameterSets(encoded, mime);
                            if (extracted != null && extracted.length > 0) {
                                csd = extracted;
                            }
                        }
                        if (csd != null && csd.length > 0 && !startsWith(effective, csd)) {
                            effective = concat(csd, effective);
                        }
                    }
                    fire(effective, info.presentationTimeUs, false, keyFrame);
                }
            } catch (Exception e) {
                error("drainOutput failed: " + e.getMessage());
            } finally {
                try {
                    codec.releaseOutputBuffer(outputIndex, false);
                } catch (Exception ignored) {
                }
            }
        }
    }

    private void fire(byte[] data, long ptsUs, boolean config, boolean keyFrame) {
        try {
            if (callback != null) {
                callback.onEncodedFrame(data, 0, data.length, ptsUs, config, keyFrame, mime);
            }
        } catch (Throwable t) {
            error("callback failed: " + t.getMessage());
        }
    }

    private void error(String message) {
        try {
            if (callback != null) callback.onError(message);
        } catch (Throwable ignored) {
        }
    }

    private void releaseCodec() {
        MediaCodec c = codec;
        codec = null;
        if (c != null) {
            try {
                c.stop();
            } catch (Exception ignored) {
            }
            try {
                c.release();
            } catch (Exception ignored) {
            }
        }
    }

    private static byte[] toNv12(byte[] rgba, int width, int height, boolean flip) {
        int frameSize = width * height;
        byte[] nv12 = new byte[frameSize + frameSize / 2];
        int yIndex = 0;
        int uvIndex = frameSize;

        for (int y = 0; y < height; y++) {
            int srcRow = flip ? height - 1 - y : y;
            int srcOffset = srcRow * width * 4;

            for (int x = 0; x < width; x++) {
                int r = rgba[srcOffset + x * 4] & 0xff;
                int g = rgba[srcOffset + x * 4 + 1] & 0xff;
                int b = rgba[srcOffset + x * 4 + 2] & 0xff;

                int yv = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                nv12[yIndex++] = (byte) clamp(yv, 0, 255);

                if ((y & 1) == 0 && (x & 1) == 0) {
                    int u = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                    int v = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                    nv12[uvIndex++] = (byte) clamp(u, 0, 255);
                    nv12[uvIndex++] = (byte) clamp(v, 0, 255);
                }
            }
        }
        return nv12;
    }

    private static int clamp(int value, int min, int max) {
        return value < min ? min : (value > max ? max : value);
    }

    private static byte[] ensureAnnexB(byte[] data) {
        if (data.length >= 4 && startsWith(data, START_CODE_4)) return data;
        if (data.length >= 3 && startsWith(data, START_CODE_3)) return data;

        ByteArrayOutputStream out = new ByteArrayOutputStream(data.length + 64);
        int i = 0;
        try {
            while (i + 4 <= data.length) {
                int len = ((data[i] & 0xff) << 24) |
                        ((data[i + 1] & 0xff) << 16) |
                        ((data[i + 2] & 0xff) << 8) |
                        (data[i + 3] & 0xff);
                i += 4;
                if (len < 0 || i + len > data.length) return data;
                out.write(START_CODE_4);
                out.write(data, i, len);
                i += len;
            }
        } catch (IOException ignored) {
            return data;
        }
        return out.size() > 0 ? out.toByteArray() : data;
    }

    private static boolean startsWith(byte[] data, byte[] prefix) {
        if (data.length < prefix.length) return false;
        for (int i = 0; i < prefix.length; i++) {
            if (data[i] != prefix[i]) return false;
        }
        return true;
    }

    private static byte[] concat(byte[] a, byte[] b) {
        byte[] out = new byte[a.length + b.length];
        System.arraycopy(a, 0, out, 0, a.length);
        System.arraycopy(b, 0, out, a.length, b.length);
        return out;
    }

    private static byte[] extractParameterSets(byte[] data, String mime) {
        List<byte[]> nalus = splitNalUnits(data);
        int first = -1;
        for (int i = 0; i < nalus.size(); i++) {
            if (isParameterSet(nalus.get(i), mime)) {
                first = i;
                break;
            }
        }
        if (first < 0) return null;

        int end = first;
        while (end < nalus.size() && isParameterSet(nalus.get(end), mime)) {
            end++;
        }
        if (end == first) return null;

        int total = 0;
        for (int i = first; i < end; i++) {
            total += nalus.get(i).length + START_CODE_4.length;
        }
        ByteArrayOutputStream out = new ByteArrayOutputStream(total);
        try {
            for (int i = first; i < end; i++) {
                out.write(START_CODE_4);
                out.write(nalus.get(i), 0, nalus.get(i).length);
            }
        } catch (IOException ignored) {
            return null;
        }
        return out.toByteArray();
    }

    private static boolean isParameterSet(byte[] nalu, String mime) {
        if (nalu == null || nalu.length == 0) return false;
        int type;
        if ("video/avc".equals(mime)) {
            type = nalu[0] & 0x1f;
            return type == 7 || type == 8;
        } else {
            type = (nalu[0] & 0x7e) >> 1;
            return type == 32 || type == 33 || type == 34;
        }
    }

    private static List<byte[]> splitNalUnits(byte[] data) {
        List<byte[]> nalus = new ArrayList<byte[]>();
        int start = 0;
        int i = 0;
        while (i < data.length - 3) {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1) {
                if (i > start) nalus.add(copyOfRange(data, start, i));
                start = i + 4;
                i += 4;
            } else if (i < data.length - 2 &&
                    data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1) {
                if (i > start) nalus.add(copyOfRange(data, start, i));
                start = i + 3;
                i += 3;
            } else {
                i++;
            }
        }
        if (start < data.length) nalus.add(copyOfRange(data, start, data.length));
        return nalus;
    }

    private static byte[] copyOfRange(byte[] data, int from, int to) {
        byte[] out = new byte[to - from];
        System.arraycopy(data, from, out, 0, out.length);
        return out;
    }

    private static final class Frame {
        final byte[] rgba;
        final int width;
        final int height;
        final long ptsUs;

        Frame(byte[] rgba, int width, int height, long ptsUs) {
            this.rgba = rgba;
            this.width = width;
            this.height = height;
            this.ptsUs = ptsUs;
        }
    }
}
