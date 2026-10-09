// PingCoreLatency.jslib: the browser side of the latency probe's WebGL beacon transport
// (WebGlEchoTransport.cs beside this file, compiled only for WebGL players). A WebGL player has no
// ClientWebSocket, so the probe reaches each location's beacon through the browser's WebSocket.
//
// Timing: the probe sends a ping and needs the moment the pong arrived. The C# side runs on the
// browser's only thread, so this file hands C# each message's age with its text, in microseconds:
// performance.now() just before the call minus event.timeStamp, the time the browser created the
// message event (the same clock as performance.now() in every current browser). C# subtracts the age
// from its own Stopwatch reading.
//   Captured: the time the event waited in the browser's task queue after it was created (behind a
//   Unity frame, for example), and the copy into the WASM heap before the call.
//   Not captured: anything before the browser created the event. A browser creates it on the page's
//   main thread once the frame has come off the network, so a pong that lands while that thread is
//   busy is created, and stamped, only when the thread is free; that wait stays in the measured
//   round trip. The browser also coarsens both clocks (to 5 us to 1 ms depending on the browser and
//   on cross-origin isolation). A WebGL figure can therefore be higher than a desktop one; it is
//   never lower than the network round trip.
// An implausible event.timeStamp (0, from an older epoch-based clock, or later than the handler's
// own reading) falls back to the handler's first performance.now(). The probe itself (one discarded
// warm-up ping, the median of 5 pongs, a failed location left out, never 0) is the same C# on every
// platform.
//
// Callbacks into C# (function pointers registered once by PingCoreLatency_Init):
//   onOpen(socket)                         the socket opened
//   onMessage(socket, text, bytes, ageUs)  one text message, UTF-8 at `text` (freed after the call)
//   onClose(socket, code)                  the socket closed or failed (an error event is always followed by close)
// Binary messages are skipped. A text message larger than 64 KiB closes the socket (a beacon answers four bytes).

var PingCoreLatencyLibrary = {
    $PingCoreLatency: {
        sockets: {},
        next: 1,
        onOpen: 0,
        onMessage: 0,
        onClose: 0,
        maxBytes: 65536,

        // Forgets the socket and tells C# once. Later events of the same socket are ignored.
        closed: function (id, code) {
            var socket = PingCoreLatency.sockets[id];
            if (!socket) {
                return;
            }
            delete PingCoreLatency.sockets[id];
            socket.onopen = socket.onmessage = socket.onerror = socket.onclose = null;
            if (socket.readyState === 0 || socket.readyState === 1) {
                try { socket.close(); } catch (e) { }
            }
            {{{ makeDynCall('vii', 'PingCoreLatency.onClose') }}}(id, code);
        }
    },

    PingCoreLatency_Init: function (onOpen, onMessage, onClose) {
        PingCoreLatency.onOpen = onOpen;
        PingCoreLatency.onMessage = onMessage;
        PingCoreLatency.onClose = onClose;
    },

    // Opens a socket; returns its id (1 or more), or 0 when the browser refuses the URL.
    PingCoreLatency_Connect: function (urlPtr) {
        var socket;
        try {
            socket = new WebSocket(UTF8ToString(urlPtr));
        } catch (e) {
            return 0;
        }
        var id = PingCoreLatency.next++;
        PingCoreLatency.sockets[id] = socket;
        socket.binaryType = 'arraybuffer';
        socket.onopen = function () {
            {{{ makeDynCall('vi', 'PingCoreLatency.onOpen') }}}(id);
        };
        socket.onmessage = function (event) {
            // Read first: everything after this line is not part of the round trip.
            var stamp = performance.now();
            var created = event.timeStamp;
            if (!(created > 0 && created <= stamp)) {
                created = stamp;
            }
            if (typeof event.data !== 'string') {
                return;
            }
            var bytes = lengthBytesUTF8(event.data);
            if (bytes > PingCoreLatency.maxBytes) {
                PingCoreLatency.closed(id, 1009);
                return;
            }
            var buffer = _malloc(bytes + 1);
            stringToUTF8(event.data, buffer, bytes + 1);
            var ageMicros = Math.min(2147483647, Math.max(0, Math.round((performance.now() - created) * 1000)));
            try {
                {{{ makeDynCall('viiii', 'PingCoreLatency.onMessage') }}}(id, buffer, bytes, ageMicros);
            } finally {
                _free(buffer);
            }
        };
        socket.onerror = function () {
        };
        socket.onclose = function (event) {
            PingCoreLatency.closed(id, event && event.code ? event.code : 1006);
        };
        return id;
    },

    // Sends one text message; returns 1, or 0 when the socket is not open.
    PingCoreLatency_Send: function (id, textPtr) {
        var socket = PingCoreLatency.sockets[id];
        if (!socket || socket.readyState !== 1) {
            return 0;
        }
        try {
            socket.send(UTF8ToString(textPtr));
        } catch (e) {
            return 0;
        }
        return 1;
    },

    // Closes and forgets a socket without calling back (C# disposed it).
    PingCoreLatency_Close: function (id) {
        var socket = PingCoreLatency.sockets[id];
        if (!socket) {
            return;
        }
        delete PingCoreLatency.sockets[id];
        socket.onopen = socket.onmessage = socket.onerror = socket.onclose = null;
        if (socket.readyState === 0 || socket.readyState === 1) {
            try { socket.close(); } catch (e) { }
        }
    }
};

autoAddDeps(PingCoreLatencyLibrary, '$PingCoreLatency');
mergeInto(LibraryManager.library, PingCoreLatencyLibrary);
