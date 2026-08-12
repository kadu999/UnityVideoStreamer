package com.videostream.stream;

public interface VideoStreamCallback {
    void onEncodedFrame(byte[] data, int offset, int length, long ptsUs,
                        boolean config, boolean keyFrame, String mime);
    void onError(String message);
}
