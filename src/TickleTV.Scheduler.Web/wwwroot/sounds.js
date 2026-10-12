// Button sounds. Any element with data-sound="name" plays sounds/name.mp3 when clicked.
// data-sound="keyok" picks one of keyok1, keyok2, keyok3 at random, the LCARS key click.
// Add more by dropping an .mp3 into wwwroot/sounds and setting data-sound on the control.
(function () {
    var cache = {};
    var sets = { keyok: ["keyok1", "keyok2", "keyok3"] };

    function clip(name) {
        if (!cache[name]) {
            cache[name] = new Audio("sounds/" + name + ".mp3");
            cache[name].preload = "auto";
        }
        return cache[name];
    }

    function play(name) {
        var pick = sets[name] ? sets[name][Math.floor(Math.random() * sets[name].length)] : name;
        var a = clip(pick);
        try { a.currentTime = 0; a.play().catch(function () { /* the browser may block sound until the first click */ }); }
        catch (e) { /* no sound is not an error */ }
    }

    document.addEventListener("click", function (ev) {
        var el = ev.target && ev.target.closest ? ev.target.closest("[data-sound]") : null;
        if (el) play(el.getAttribute("data-sound"));
    }, true);

    window.tickleSounds = { play: play };
})();
