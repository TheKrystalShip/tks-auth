package com.thekrystalship.shell;

import android.app.Activity;
import android.content.ActivityNotFoundException;
import android.content.Intent;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Message;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.webkit.DownloadListener;
import android.webkit.ServiceWorkerClient;
import android.webkit.ValueCallback;
import android.webkit.ServiceWorkerController;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.FrameLayout;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;

/**
 * A site in an app: the site in a WebView, with what a browser's page cannot do done natively. The
 * page reaches the shell as {@code window.TksShell} ({@link Bridge}): downloads that run with no page
 * ({@link DownloadService}), carrying on with the screen off and the app out of view, read back from
 * the site's own origin ({@link LocalFiles}). The user agent carries the app's mark and version
 * ({@link Built#MARK}) and {@code TksShell/<version>}.
 *
 * Every page loads here, the sign-in's among them, so the session the site keeps in its storage is
 * the app's. A link that opens a new window goes to the phone's browser. Video the page puts full
 * screen fills the screen, with the system bars hidden, until it leaves; otherwise the page is kept
 * clear of the bars, the display cutout and the keyboard. With no connection the site's service worker
 * answers its pages; a site that cannot be reached and has no such answer is shown with a way to try
 * again. Back goes back a page, and leaves from the first.
 */
public final class ShellActivity extends Activity {
    private static final String UNREACHABLE = "file:///android_asset/unreachable.html";
    private static final String TONE = "tone";
    private static final int QUIET_TRIES = 2;
    private static final int QUIET_AFTER_MS = 1500;
    private static final int PICK = 1;

    private FrameLayout frame;
    private WebView web;
    private Origin origin;
    private LocalFiles files;
    private View fullScreen;
    private WebChromeClient.CustomViewCallback leaveFullScreen;
    /** The address of the page the WebView last began loading, read by the bridge on its own thread. */
    private volatile Uri showing;
    /** How many times in a row a page of the site has failed to load. */
    private int failures;
    /** What the shell runs in each page of the site (assets/page.js). */
    private String pageScript;
    /** The file input waiting on the system's picker, if one is. */
    private ValueCallback<Uri[]> picking;
    /** The launcher's icon, in the palette the page shows. */
    private LauncherIcon launcherIcon;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        origin = Origin.of(getIntent());
        launcherIcon = new LauncherIcon(this);
        Downloads downloads = Downloads.of(this);
        files = new LocalFiles(origin, downloads);
        WebView.setWebContentsDebuggingEnabled(Built.DEBUG);

        int tone = getSharedPreferences("shell", MODE_PRIVATE).getInt(TONE, getColor(R.color.icon_background));
        frame = new FrameLayout(this);
        web = new WebView(this);
        paint(tone);
        WebSettings settings = web.getSettings();
        settings.setJavaScriptEnabled(true);
        settings.setDomStorageEnabled(true);
        settings.setMediaPlaybackRequiresUserGesture(false);
        settings.setSupportMultipleWindows(true);
        settings.setUserAgentString(settings.getUserAgentString() + " " + Built.MARK + "/" + Built.VERSION + " TksShell/" + Bridge.VERSION);
        web.setWebViewClient(new Client());
        web.setWebChromeClient(new Chrome());
        final SavedFiles saved = new SavedFiles(this);
        pageScript = asset("page.js");
        web.addJavascriptInterface(new Bridge(this, origin, downloads, saved), Bridge.NAME);
        // A file the page asks the WebView to download: one at an address goes to the system's download
        // manager; one the page made itself is saved by the page's script, under the name it comes with.
        web.setDownloadListener(new DownloadListener() {
            @Override
            public void onDownloadStart(String url, String userAgent, String disposition, String type, long length) {
                if (url.startsWith("http://") || url.startsWith("https://")) {
                    saved.fetch(url, userAgent, disposition, type);
                } else if (url.startsWith("blob:") || url.startsWith("data:")) {
                    String name = android.webkit.URLUtil.guessFileName(url, disposition, type);
                    web.evaluateJavascript("(function(){var a=document.createElement('a');a.href=" + quoted(url)
                        + ";a.download=" + quoted(name) + ";document.body.appendChild(a);a.click();a.remove();})()", null);
                }
            }
        });
        // A request a service worker makes reaches the app here rather than through the page's client.
        ServiceWorkerController.getInstance().setServiceWorkerClient(new ServiceWorkerClient() {
            @Override
            public WebResourceResponse shouldInterceptRequest(WebResourceRequest request) {
                return files.answer(request);
            }
        });
        frame.addView(web, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        // Android draws the app under the system bars and the keyboard; the page is kept clear of them.
        frame.setOnApplyWindowInsetsListener(new View.OnApplyWindowInsetsListener() {
            @Override
            public WindowInsets onApplyWindowInsets(View view, WindowInsets insets) {
                if (Build.VERSION.SDK_INT >= 30) {
                    android.graphics.Insets bars = insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.ime()
                        | WindowInsets.Type.displayCutout());
                    view.setPadding(bars.left, bars.top, bars.right, bars.bottom);
                }
                return WindowInsets.CONSUMED;
            }
        });
        setContentView(frame);
        web.loadUrl(origin.start());
    }

    @Override
    public void onBackPressed() {
        if (fullScreen != null) {
            leaveFullScreen.onCustomViewHidden();
            return;
        }
        if (web.canGoBack()) web.goBack();
        else super.onBackPressed();
    }

    /** What the person picked for a file input, or nothing when they backed out. */
    @Override
    protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (request != PICK || picking == null) return;
        Uri[] chosen = null;
        if (result == RESULT_OK && data != null && data.getClipData() != null) {
            chosen = new Uri[data.getClipData().getItemCount()];
            for (int i = 0; i < chosen.length; i++) chosen[i] = data.getClipData().getItemAt(i).getUri();
        } else {
            chosen = WebChromeClient.FileChooserParams.parseResult(result, data);
        }
        picking.onReceiveValue(chosen);
        picking = null;
    }

    private String asset(String name) {
        try (InputStream in = getAssets().open(name)) {
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            byte[] buffer = new byte[8192];
            for (int n; (n = in.read(buffer)) > 0; ) out.write(buffer, 0, n);
            return new String(out.toByteArray(), StandardCharsets.UTF_8);
        } catch (IOException missing) {
            return "";
        }
    }

    private static String quoted(String text) {
        return org.json.JSONObject.quote(text);
    }

    @Override
    protected void onResume() {
        super.onResume();
        web.onResume();
        // What a timed-out or stopped service left is taken up again whenever the app is in view.
        DownloadService.start(this);
    }

    @Override
    protected void onPause() {
        web.onPause();
        super.onPause();
    }

    @Override
    protected void onStop() {
        launcherIcon.apply();
        super.onStop();
    }

    @Override
    protected void onDestroy() {
        web.destroy();
        super.onDestroy();
    }

    /** The palette the page shows, which the launcher's icon takes once the app leaves the screen. */
    void palette(String palette) {
        launcherIcon.want(palette);
    }

    /**
     * The colour the page shows: the strips behind the system bars and the WebView's ground take it,
     * the bars' icons are drawn to read on it, and the next launch starts with it.
     */
    void tone(final int colour) {
        runOnUiThread(new Runnable() {
            @Override
            public void run() {
                paint(colour);
            }
        });
        getSharedPreferences("shell", MODE_PRIVATE).edit().putInt(TONE, colour).apply();
    }

    @SuppressWarnings("deprecation")
    private void paint(int colour) {
        frame.setBackgroundColor(colour);
        web.setBackgroundColor(colour);
        getWindow().getDecorView().setBackgroundColor(colour);
        // Android draws a scrim of its own behind three-button navigation unless told the app gives the
        // buttons their contrast, which it does here by the icons' shade.
        if (Build.VERSION.SDK_INT >= 29) getWindow().setNavigationBarContrastEnforced(false);
        double r = ((colour >> 16) & 0xFF) / 255.0, g = ((colour >> 8) & 0xFF) / 255.0, b = (colour & 0xFF) / 255.0;
        boolean light = 0.2126 * r + 0.7152 * g + 0.0722 * b > 0.5;
        if (Build.VERSION.SDK_INT >= 30) {
            WindowInsetsController insets = getWindow().getInsetsController();
            if (insets == null) return;
            int dark = WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS | WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS;
            insets.setSystemBarsAppearance(light ? dark : 0, dark);
        } else {
            View decor = getWindow().getDecorView();
            int flags = decor.getSystemUiVisibility();
            int lightBars = View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR | (Build.VERSION.SDK_INT >= 26 ? View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR : 0);
            decor.setSystemUiVisibility(light ? flags | lightBars : flags & ~lightBars);
        }
    }

    @SuppressWarnings("deprecation")
    private void bars(boolean shown) {
        if (Build.VERSION.SDK_INT < 30) {
            getWindow().getDecorView().setSystemUiVisibility(shown ? 0
                : View.SYSTEM_UI_FLAG_FULLSCREEN | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY);
            return;
        }
        WindowInsetsController insets = getWindow().getInsetsController();
        if (insets == null) return;
        if (shown) {
            insets.show(WindowInsets.Type.systemBars());
        } else {
            insets.hide(WindowInsets.Type.systemBars());
            insets.setSystemBarsBehavior(WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
        }
    }

    private void openOutside(Uri address) {
        try {
            startActivity(new Intent(Intent.ACTION_VIEW, address));
        } catch (ActivityNotFoundException nothing) {
            // No browser on the phone: the link stays unopened.
        }
    }

    /** Whether the page showing is the site's: the bridge answers nothing else, the sign-in's pages among them. */
    boolean onSite() {
        return origin.owns(showing);
    }

    private final class Client extends WebViewClient {
        @Override
        public void onPageStarted(WebView view, String url, android.graphics.Bitmap favicon) {
            showing = Uri.parse(url);
        }

        @Override
        public void doUpdateVisitedHistory(WebView view, String url, boolean reload) {
            showing = Uri.parse(url);
        }

        /** Pages load here, the site's and its sign-in's; anything that is not a web page goes to the phone. */
        @Override
        public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
            String scheme = request.getUrl().getScheme();
            if ("https".equals(scheme) || origin.owns(request.getUrl())) return false;
            openOutside(request.getUrl());
            return true;
        }

        @Override
        public WebResourceResponse shouldInterceptRequest(WebView view, WebResourceRequest request) {
            return files.answer(request);
        }

        /**
         * A page of the site that fails is tried twice more, quietly, before the app says it cannot reach
         * the site: on a cold start with no connection, the first loads fail before the site's service
         * worker, which answers every page with no connection, is running, which takes a second or two.
         */
        @Override
        public void onReceivedError(WebView view, WebResourceRequest request, WebResourceError error) {
            if (!request.isForMainFrame()) return;
            boolean quietly = origin.owns(request.getUrl()) && failures < QUIET_TRIES;
            failures++;
            unreachable(view, request.getUrl(), quietly);
        }

        @Override
        public void onPageCommitVisible(WebView view, String url) {
            if (origin.owns(Uri.parse(url))) failures = 0;
        }

        @Override
        public void onPageFinished(WebView view, String url) {
            if (origin.owns(Uri.parse(url)) && !pageScript.isEmpty()) view.evaluateJavascript(pageScript, null);
        }

        @Override
        public void onReceivedHttpError(WebView view, WebResourceRequest request, WebResourceResponse response) {
            if (request.isForMainFrame() && origin.owns(request.getUrl()) && response.getStatusCode() >= 500) unreachable(view, request.getUrl(), false);
        }

        private void unreachable(WebView view, Uri failed, boolean quietly) {
            String again = origin.owns(failed) ? failed.toString() : origin.start();
            view.loadUrl(UNREACHABLE + "?host=" + Uri.encode(origin.host()) + "&to=" + Uri.encode(again)
                + (quietly ? "&quiet=1&after=" + QUIET_AFTER_MS : ""));
        }
    }

    private final class Chrome extends WebChromeClient {
        @Override
        public void onShowCustomView(View view, CustomViewCallback callback) {
            if (fullScreen != null) {
                callback.onCustomViewHidden();
                return;
            }
            fullScreen = view;
            leaveFullScreen = callback;
            frame.addView(view, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
            bars(false);
        }

        @Override
        public void onHideCustomView() {
            if (fullScreen == null) return;
            frame.removeView(fullScreen);
            fullScreen = null;
            leaveFullScreen = null;
            bars(true);
        }

        /** A file input opens the system's picker, for as many files as the input takes. */
        @Override
        public boolean onShowFileChooser(WebView view, ValueCallback<Uri[]> callback, FileChooserParams params) {
            if (picking != null) picking.onReceiveValue(null);
            picking = callback;
            Intent intent = params.createIntent();
            if (params.getMode() == FileChooserParams.MODE_OPEN_MULTIPLE) intent.putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true);
            try {
                startActivityForResult(intent, PICK);
            } catch (ActivityNotFoundException none) {
                picking = null;
                return false;
            }
            return true;
        }

        /** A link that asks for a new window opens in the phone's browser. */
        @Override
        public boolean onCreateWindow(WebView view, boolean dialog, boolean gesture, Message result) {
            WebView probe = new WebView(view.getContext());
            probe.setWebViewClient(new WebViewClient() {
                @Override
                public boolean shouldOverrideUrlLoading(WebView v, WebResourceRequest request) {
                    openOutside(request.getUrl());
                    probe.destroy();
                    return true;
                }
            });
            ((WebView.WebViewTransport) result.obj).setWebView(probe);
            result.sendToTarget();
            return true;
        }
    }
}
