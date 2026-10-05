(function registerHelloLiveRemoteStorage() {
    globalThis.helloLiveRemoteStorageGetItem = function getItem(key) {
        try {
            return globalThis.localStorage?.getItem(key) ?? null;
        } catch (error) {
            console.warn('HelloLive: localStorage read failed.', error);
            return null;
        }
    };

    globalThis.helloLiveRemoteStorageSetItem = function setItem(key, value) {
        try {
            globalThis.localStorage?.setItem(key, value ?? '');
        } catch (error) {
            console.warn('HelloLive: localStorage write failed.', error);
        }
    };
})();
