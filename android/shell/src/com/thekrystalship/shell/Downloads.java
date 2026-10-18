package com.thekrystalship.shell;

import android.content.Context;
import android.util.Log;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.io.RandomAccessFile;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.Map;

/**
 * The files the page asked the shell to download, kept under the app's own files:
 * {@code downloads/<id>/download.json}, what the page handed over and how far it has got, beside each
 * file by its name.
 *
 * A download is {@code {id, title, headers, files: [{name, url, bytes, type?, validator?}], done?: {url}}}
 * ({@link #add}): the files to fetch, each with its exact length, and the headers every request
 * carries, which is how a site lets the shell fetch for hours with no page to renew a credential.
 * One download is fetched at a time, one file after another, by {@link #work}, which the foreground
 * service runs. A file resumes from its length on disk, by {@code Range} with {@code If-Range} naming
 * the validator the bytes on disk arrived under; a whole {@code 200} to a resume means the file
 * changed, and it starts over. A connection that breaks or goes quiet for {@link #READ_TIMEOUT_MS} is
 * tried again, after a pause that grows to a minute and ends early when a network comes back
 * ({@link #networkBack}), for as long as the download is not paused: a phone loses its connection all
 * the time and gets it back. An answer only the page can do anything about fails the download with a
 * reason the page words ({@code credential} for a 401 or 403, {@code gone} for a 404, 409 or 410,
 * {@code http-<status>} for any other refusal it cannot outwait). A download whose every file is
 * whole is said to {@code done.url}, a POST with the same headers, so the site hears of it with no
 * page running.
 *
 * Everything here is one lock: the page's calls arrive on the WebView's bridge thread and the
 * transfer runs on the service's.
 */
final class Downloads {
    private static final String TAG = "Downloads";
    private static final String RECORD = "download.json";
    static final int CONNECT_TIMEOUT_MS = 15000;
    static final int READ_TIMEOUT_MS = 30000;
    private static final long[] BACKOFF_MS = {2000, 5000, 15000, 30000, 60000};
    private static final int BUFFER = 256 * 1024;
    private static final long PROGRESS_MS = 500;

    private static final Runnable NOTHING = new Runnable() {
        @Override
        public void run() {
        }
    };

    private static Downloads instance;

    private final File root;
    private final Map<String, JSONObject> downloads = new LinkedHashMap<>();
    /** What each download is doing now, by id: not kept, rebuilt from the records when the process starts. */
    private final Map<String, String> states = new LinkedHashMap<>();
    private final Map<String, String> reasons = new LinkedHashMap<>();
    private String running;
    private HttpURLConnection connection;
    private Runnable changed = NOTHING;
    private Finished finished = null;
    /** What a pause between attempts waits on, so a network coming back ends it at once. */
    private final Object nap = new Object();

    private Downloads(Context context) {
        root = new File(context.getFilesDir(), "downloads");
        File[] dirs = root.listFiles();
        if (dirs == null) return;
        for (File dir : dirs) {
            JSONObject download = read(new File(dir, RECORD));
            if (download == null) continue;
            String id = download.optString("id");
            downloads.put(id, download);
            states.put(id, download.has("completedAt") ? "done" : download.optBoolean("paused") ? "paused" : "queued");
            if (download.has("failed")) {
                states.put(id, "failed");
                reasons.put(id, download.optString("failed"));
            }
        }
    }

    static synchronized Downloads of(Context context) {
        if (instance == null) instance = new Downloads(context.getApplicationContext());
        return instance;
    }

    /** What hears that a download is whole: its id and the title the page gave it. */
    interface Finished {
        void finished(String id, String title);
    }

    /** Called with each download that becomes whole, on the transfer's thread. */
    synchronized void onFinished(Finished listener) {
        finished = listener;
    }

    /** Called after every change a person could see, from whichever thread made it. */
    synchronized void onChanged(Runnable listener) {
        changed = listener != null ? listener : NOTHING;
    }

    /**
     * Takes a download over from the page, its URLs absolute. One already here by the same id keeps
     * the bytes it holds of each file, which the server judges by their validator, and takes the new
     * headers.
     */
    synchronized void add(JSONObject handed) throws JSONException {
        String id = handed.getString("id");
        File dir = dirOf(id);
        if (!dir.isDirectory() && !dir.mkdirs()) throw new JSONException("No room for " + id);
        JSONObject before = downloads.get(id);
        JSONArray files = handed.getJSONArray("files");
        for (int i = 0; i < files.length(); i++) {
            JSONObject file = files.getJSONObject(i);
            JSONObject held = before != null ? fileEntry(before, file.getString("name")) : null;
            if (held != null && held.has("validator")) file.put("validator", held.getString("validator"));
        }
        handed.remove("paused");
        handed.remove("failed");
        handed.remove("completedAt");
        handed.remove("told");
        downloads.put(id, handed);
        write(id);
        states.put(id, "queued");
        reasons.remove(id);
        changed.run();
    }

    synchronized void pause(String id) {
        JSONObject download = downloads.get(id);
        if (download == null || download.has("completedAt")) return;
        put(download, "paused", true);
        write(id);
        states.put(id, "paused");
        if (id.equals(running)) cut();
        changed.run();
    }

    synchronized void resume(String id) {
        JSONObject download = downloads.get(id);
        if (download == null || download.has("completedAt")) return;
        download.remove("paused");
        download.remove("failed");
        write(id);
        states.put(id, "queued");
        reasons.remove(id);
        changed.run();
    }

    synchronized void remove(String id) {
        downloads.remove(id);
        states.remove(id);
        reasons.remove(id);
        if (id.equals(running)) cut();
        delete(dirOf(id));
        changed.run();
    }

    /**
     * One download as the page reads it, or null: {@code {id, state, received, bytes, reason?, files:
     * [{name, received, bytes}]}}, {@code state} one of {@code queued}, {@code downloading},
     * {@code waiting}, {@code paused}, {@code failed}, {@code done}.
     */
    synchronized JSONObject status(String id) {
        JSONObject download = downloads.get(id);
        if (download == null) return null;
        long received = 0;
        long bytes = 0;
        JSONArray files = new JSONArray();
        JSONArray listed = download.optJSONArray("files");
        for (int i = 0; listed != null && i < listed.length(); i++) {
            JSONObject entry = listed.optJSONObject(i);
            long want = entry.optLong("bytes");
            long have = Math.min(fileOf(id, entry.optString("name")).length(), want);
            received += have;
            bytes += want;
            JSONObject f = new JSONObject();
            put(f, "name", entry.optString("name"));
            put(f, "received", have);
            put(f, "bytes", want);
            files.put(f);
        }
        JSONObject out = new JSONObject();
        put(out, "id", id);
        put(out, "state", states.get(id));
        put(out, "received", received);
        put(out, "bytes", bytes);
        put(out, "files", files);
        if (reasons.containsKey(id)) put(out, "reason", reasons.get(id));
        return out;
    }

    /** Every download's status, by id. */
    synchronized JSONObject statuses() {
        JSONObject out = new JSONObject();
        for (String id : downloads.keySet()) put(out, id, status(id));
        return out;
    }

    /** Whether anything is left to fetch: a download neither done, paused nor failed. */
    synchronized boolean pending() {
        for (String state : states.values()) if ("queued".equals(state) || "downloading".equals(state) || "waiting".equals(state)) return true;
        return false;
    }

    /** What the notification says: the download being fetched, how far, and how many wait behind it. */
    synchronized Progress progress() {
        String id = running;
        int more = 0;
        for (Map.Entry<String, String> e : states.entrySet()) {
            if (e.getKey().equals(id)) continue;
            if ("queued".equals(e.getValue()) || "waiting".equals(e.getValue())) more++;
        }
        if (id == null || !downloads.containsKey(id)) return new Progress(null, 0, 0, more, false);
        JSONObject status = status(id);
        return new Progress(downloads.get(id).optString("title", id),
            status.optLong("received"), status.optLong("bytes"), more, "waiting".equals(states.get(id)));
    }

    /** A file of a download, under its directory by its name. */
    File fileOf(String id, String name) {
        return new File(dirOf(id), safe(name));
    }

    /** The type the page gave a download's file, or {@code application/octet-stream}. */
    synchronized String typeOf(String id, String name) {
        JSONObject download = downloads.get(id);
        JSONObject entry = download != null ? fileEntry(download, name) : null;
        return entry != null ? entry.optString("type", "application/octet-stream") : "application/octet-stream";
    }

    /** Whether a download's file is whole on disk, which is when the page may read it. */
    synchronized boolean whole(String id, String name) {
        JSONObject download = downloads.get(id);
        JSONObject entry = download != null ? fileEntry(download, name) : null;
        return entry != null && fileOf(id, name).length() == entry.optLong("bytes");
    }

    // ---- The transfer, on the service's thread ----

    /** Fetches every download that is due, one after another, until none is left or the service is stopped. */
    void work(Thread self) {
        while (!self.isInterrupted()) {
            String id = next();
            if (id == null) return;
            try {
                fetch(id, self);
            } catch (InterruptedException stopped) {
                return;
            } finally {
                synchronized (this) {
                    running = null;
                    connection = null;
                }
            }
        }
    }

    private synchronized String next() {
        for (Map.Entry<String, String> e : states.entrySet()) {
            if ("queued".equals(e.getValue()) || "waiting".equals(e.getValue())) {
                running = e.getKey();
                states.put(running, "downloading");
                changed.run();
                return running;
            }
        }
        return null;
    }

    private void fetch(String id, Thread self) throws InterruptedException {
        JSONObject download;
        synchronized (this) {
            download = downloads.get(id);
        }
        if (download == null) return;
        JSONArray files = download.optJSONArray("files");
        for (int i = 0; files != null && i < files.length(); i++) {
            if (!stillMine(id)) return;
            if (file(id, download, files.optJSONObject(i), self) != Outcome.WHOLE) return;
        }
        done(id, download);
    }

    private enum Outcome { WHOLE, STOPPED, FAILED }

    private Outcome file(String id, JSONObject download, JSONObject entry, Thread self) throws InterruptedException {
        String name = entry.optString("name");
        long bytes = entry.optLong("bytes");
        File file = fileOf(id, name);
        int attempt = 0;
        while (stillMine(id)) {
            long at = file.length();
            if (at > bytes) {
                truncate(file);
                at = 0;
            }
            if (at == bytes) return Outcome.WHOLE;
            HttpURLConnection http = null;
            try {
                http = open(entry.getString("url"), download.optJSONObject("headers"));
                if (at > 0) {
                    http.setRequestProperty("Range", "bytes=" + at + "-");
                    if (entry.has("validator")) http.setRequestProperty("If-Range", entry.getString("validator"));
                }
                synchronized (this) {
                    if (!id.equals(running)) return Outcome.STOPPED;
                    connection = http;
                }
                int code = http.getResponseCode();
                if (code == 401 || code == 403) return failed(id, "credential");
                if (code == 404 || code == 409 || code == 410) return failed(id, "gone");
                if (code == 429 || code == 503) {
                    waiting(id);
                    rest(retryAfter(http) * 1000L);
                    continue;
                }
                if (code >= 400 && code < 500) return failed(id, "http-" + code);
                if (code == 200 && at > 0) {
                    // The file changed under the bytes on disk: they go, and this answer is the file.
                    truncate(file);
                    at = 0;
                }
                if (code == 206 && !String.valueOf(http.getHeaderField("Content-Range")).startsWith("bytes " + at + "-")) {
                    truncate(file);
                    continue;
                }
                if (code != 200 && code != 206) throw new IOException("The server answered " + code);
                String validator = http.getHeaderField("ETag");
                if (validator != null && !validator.equals(entry.optString("validator", null))) {
                    synchronized (this) {
                        put(entry, "validator", validator);
                        write(id);
                    }
                }
                downloading(id);
                copy(http.getInputStream(), file, at, bytes);
                attempt = 0;
            } catch (IOException | JSONException broken) {
                if (!stillMine(id)) return Outcome.STOPPED;
                Log.i(TAG, id + " " + name + " broke at " + file.length() + ": " + broken.getMessage());
                waiting(id);
                rest(BACKOFF_MS[Math.min(attempt++, BACKOFF_MS.length - 1)]);
            } finally {
                if (http != null) http.disconnect();
                synchronized (this) {
                    connection = null;
                }
            }
            if (self.isInterrupted()) throw new InterruptedException();
        }
        return Outcome.STOPPED;
    }

    private HttpURLConnection open(String url, JSONObject headers) throws IOException {
        HttpURLConnection http = (HttpURLConnection) new URL(url).openConnection();
        if (headers != null) {
            for (Iterator<String> keys = headers.keys(); keys.hasNext(); ) {
                String key = keys.next();
                http.setRequestProperty(key, headers.optString(key));
            }
        }
        http.setConnectTimeout(CONNECT_TIMEOUT_MS);
        http.setReadTimeout(READ_TIMEOUT_MS);
        http.setUseCaches(false);
        return http;
    }

    private void copy(InputStream in, File file, long at, long bytes) throws IOException {
        byte[] buffer = new byte[BUFFER];
        long last = 0;
        try (RandomAccessFile out = new RandomAccessFile(file, "rw"); InputStream body = in) {
            out.seek(at);
            long done = at;
            while (done < bytes) {
                int n = body.read(buffer, 0, (int) Math.min(buffer.length, bytes - done));
                if (n < 0) break;
                out.write(buffer, 0, n);
                done += n;
                long now = System.currentTimeMillis();
                if (now - last >= PROGRESS_MS) {
                    last = now;
                    changed();
                }
            }
            out.getFD().sync();
        }
        changed();
    }

    /** Marks a download whole, and says so at its {@code done.url}; the page says it again for one this could not. */
    private void done(String id, JSONObject download) {
        synchronized (this) {
            if (!id.equals(running)) return;
            put(download, "completedAt", System.currentTimeMillis());
            write(id);
            states.put(id, "done");
            if (finished != null) finished.finished(id, download.optString("title", id));
        }
        changed();
        JSONObject done = download.optJSONObject("done");
        if (done == null || !done.has("url")) return;
        HttpURLConnection http = null;
        try {
            http = open(done.getString("url"), download.optJSONObject("headers"));
            http.setRequestMethod("POST");
            http.setDoOutput(true);
            http.setFixedLengthStreamingMode(0);
            http.getOutputStream().close();
            if (http.getResponseCode() / 100 == 2) {
                synchronized (this) {
                    put(download, "told", true);
                    write(id);
                }
            }
        } catch (IOException | JSONException unreached) {
            Log.i(TAG, id + " finished, and its site could not be told: " + unreached.getMessage());
        } finally {
            if (http != null) http.disconnect();
        }
    }

    /** Waits between attempts, until the time is up or a network comes back. */
    private void rest(long ms) throws InterruptedException {
        synchronized (nap) {
            nap.wait(ms);
        }
    }

    /** A network is there again: a transfer waiting between attempts tries at once. */
    void networkBack() {
        synchronized (nap) {
            nap.notifyAll();
        }
    }

    /** Whether the transfer of a download is still wanted: a pause or a removal lets go of it. */
    private synchronized boolean stillMine(String id) {
        return id.equals(running) && downloads.containsKey(id);
    }

    private synchronized void downloading(String id) {
        if (!id.equals(running)) return;
        states.put(id, "downloading");
        changed.run();
    }

    private synchronized void waiting(String id) {
        if (!id.equals(running)) return;
        states.put(id, "waiting");
        changed.run();
    }

    private synchronized Outcome failed(String id, String reason) {
        JSONObject download = downloads.get(id);
        if (download == null) return Outcome.STOPPED;
        put(download, "failed", reason);
        write(id);
        states.put(id, "failed");
        reasons.put(id, reason);
        changed.run();
        return Outcome.FAILED;
    }

    private synchronized void changed() {
        changed.run();
    }

    /** Stops the transfer under way at once: its connection is closed under the reading thread. */
    private void cut() {
        running = null;
        final HttpURLConnection open = connection;
        connection = null;
        if (open == null) return;
        new Thread(new Runnable() {
            @Override
            public void run() {
                open.disconnect();
            }
        }).start();
    }

    private static int retryAfter(HttpURLConnection http) {
        try {
            return Math.max(1, Integer.parseInt(String.valueOf(http.getHeaderField("Retry-After")).trim()));
        } catch (NumberFormatException none) {
            return 10;
        }
    }

    // ---- The records ----

    private static String safe(String name) {
        return name.replaceAll("[^A-Za-z0-9._-]", "_").replaceAll("^\\.+", "_");
    }

    private File dirOf(String id) {
        return new File(root, safe(id));
    }

    private static JSONObject fileEntry(JSONObject download, String name) {
        JSONArray files = download.optJSONArray("files");
        for (int i = 0; files != null && i < files.length(); i++) {
            JSONObject entry = files.optJSONObject(i);
            if (entry != null && name.equals(entry.optString("name"))) return entry;
        }
        return null;
    }

    /** Writes a download's record whole to a file beside it, then puts it in place, so a reader never meets half of one. */
    private void write(String id) {
        JSONObject download = downloads.get(id);
        if (download == null) return;
        File dir = dirOf(id);
        File temp = new File(dir, RECORD + ".tmp");
        try (OutputStream out = new FileOutputStream(temp)) {
            out.write(download.toString().getBytes(StandardCharsets.UTF_8));
            out.flush();
        } catch (IOException failed) {
            Log.w(TAG, "Could not write " + id + ": " + failed.getMessage());
            return;
        }
        if (!temp.renameTo(new File(dir, RECORD))) Log.w(TAG, "Could not put " + id + "'s record in place");
    }

    private static JSONObject read(File file) {
        try (InputStream in = new java.io.FileInputStream(file)) {
            byte[] all = new byte[(int) file.length()];
            int at = 0;
            while (at < all.length) {
                int n = in.read(all, at, all.length - at);
                if (n < 0) break;
                at += n;
            }
            return new JSONObject(new String(all, 0, at, StandardCharsets.UTF_8));
        } catch (IOException | JSONException unreadable) {
            return null;
        }
    }

    private static void truncate(File file) {
        try (RandomAccessFile f = new RandomAccessFile(file, "rw")) {
            f.setLength(0);
        } catch (IOException ignored) {
            // A file that cannot be opened is started again from nothing by the next request.
        }
    }

    private static void delete(File file) {
        File[] children = file.listFiles();
        if (children != null) for (File child : children) delete(child);
        //noinspection ResultOfMethodCallIgnored
        file.delete();
    }

    private static void put(JSONObject object, String key, Object value) {
        try {
            object.put(key, value);
        } catch (JSONException impossible) {
            throw new IllegalStateException(impossible);
        }
    }

    /** What the download notification shows. */
    static final class Progress {
        final String title;
        final long received;
        final long bytes;
        final int more;
        final boolean waiting;

        Progress(String title, long received, long bytes, int more, boolean waiting) {
            this.title = title;
            this.received = received;
            this.bytes = bytes;
            this.more = more;
            this.waiting = waiting;
        }
    }
}
