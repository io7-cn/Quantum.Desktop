window.quantum = window.quantum || {};
window.quantum.navigationOrder = {
  registrations: new Map(),

  initialize(id, root, reference) {
    this.dispose(id);
    let active = null;
    let frame = null;
    const rows = () =>
      Array.from(root.querySelectorAll('.navigation-order-row[data-navigation-key]'));
    const clearTargets = () => {
      for (const row of rows()) {
        row.classList.remove('drop-before', 'drop-after');
      }
    };
    const finish = (commit) => {
      const state = active;
      active = null;
      if (!state) {
        return;
      }
      cancelAnimationFrame(frame);
      frame = null;
      clearTargets();
      state.row.classList.remove('dragging');
      state.preview?.remove();
      if (state.handle.hasPointerCapture?.(state.pointerId)) {
        state.handle.releasePointerCapture(state.pointerId);
      }
      if (commit && state.dragging && state.target && root.contains(state.row)) {
        reference
          .invokeMethodAsync('MoveByPointerAsync', state.key, state.target, state.after)
          .catch((error) => {
            if (this.registrations.has(id)) {
              console.error('Could not apply sidebar drag order.', error);
            }
          });
      }
    };
    const updateTarget = () => {
      clearTargets();
      active.target = null;
      const list = root.querySelector('.navigation-order-list');
      if (!list || !root.contains(active.row)) {
        finish(false);
        return;
      }
      const bounds = list.getBoundingClientRect();
      if (
        active.x < bounds.left ||
        active.x > bounds.right ||
        active.y < bounds.top ||
        active.y > bounds.bottom
      ) {
        return;
      }
      const candidates = rows().filter((row) => row !== active.row);
      const next = candidates.find((row) => {
        const rectangle = row.getBoundingClientRect();
        return active.y < rectangle.top + rectangle.height / 2;
      });
      const target = next ?? candidates.at(-1);
      if (target) {
        active.target = target.dataset.navigationKey;
        active.after = !next;
        target.classList.add(next ? 'drop-before' : 'drop-after');
      }
    };
    const scroll = () => {
      if (!active?.dragging) {
        return;
      }
      const bounds = root.querySelector('.navigation-order-list')?.getBoundingClientRect();
      if (
        bounds &&
        active.x >= bounds.left &&
        active.x <= bounds.right &&
        active.y >= 0 &&
        active.y <= window.innerHeight
      ) {
        const edge = 48;
        const delta = active.y < edge ? -12 : active.y > window.innerHeight - edge ? 12 : 0;
        if (delta) {
          window.scrollBy(0, delta);
          updateTarget();
        }
      }
      if (active) {
        frame = requestAnimationFrame(scroll);
      }
    };
    const down = (event) => {
      const handle = event.target.closest?.('.navigation-order-handle');
      const row = handle?.closest('.navigation-order-row[data-navigation-key]');
      if (
        active ||
        event.button !== 0 ||
        event.isPrimary === false ||
        !row ||
        !root.contains(row)
      ) {
        return;
      }
      event.preventDefault();
      active = {
        row,
        handle,
        key: row.dataset.navigationKey,
        pointerId: event.pointerId,
        startX: event.clientX,
        startY: event.clientY,
        x: event.clientX,
        y: event.clientY,
        dragging: false,
        target: null,
        after: false,
        preview: null,
      };
      handle.focus({ preventScroll: true });
      handle.setPointerCapture?.(event.pointerId);
    };
    const move = (event) => {
      if (!active || event.pointerId !== active.pointerId) {
        return;
      }
      if (event.pointerType === 'mouse' && event.buttons === 0) {
        finish(false);
        return;
      }
      active.x = event.clientX;
      active.y = event.clientY;
      if (!active.dragging) {
        if (Math.hypot(active.x - active.startX, active.y - active.startY) < 5) {
          return;
        }
        const rectangle = active.row.getBoundingClientRect();
        active.offset = active.startY - rectangle.top;
        active.preview = active.row.cloneNode(true);
        active.preview.removeAttribute('data-navigation-key');
        active.preview.classList.add('navigation-order-drag-preview');
        active.preview.setAttribute('aria-hidden', 'true');
        active.preview.inert = true;
        Object.assign(active.preview.style, {
          left: `${rectangle.left}px`,
          width: `${rectangle.width}px`,
          height: `${rectangle.height}px`,
        });
        document.body.appendChild(active.preview);
        active.dragging = true;
        active.row.classList.add('dragging');
        frame = requestAnimationFrame(scroll);
      }
      event.preventDefault();
      active.preview.style.top = `${active.y - active.offset}px`;
      updateTarget();
    };
    const up = (event) => {
      if (active && event.pointerId === active.pointerId) {
        if (active.dragging) {
          active.x = event.clientX;
          active.y = event.clientY;
          updateTarget();
        }
        finish(true);
      }
    };
    const cancel = (event) => {
      if (active && (event.pointerId === undefined || event.pointerId === active.pointerId)) {
        finish(false);
      }
    };
    const key = (event) => {
      if (active && event.key === 'Escape') {
        event.preventDefault();
        finish(false);
      }
    };
    root.addEventListener('pointerdown', down);
    window.addEventListener('pointermove', move, { passive: false });
    window.addEventListener('pointerup', up);
    window.addEventListener('pointercancel', cancel);
    window.addEventListener('lostpointercapture', cancel);
    window.addEventListener('blur', cancel);
    window.addEventListener('keydown', key);
    this.registrations.set(id, {
      dispose() {
        finish(false);
        root.removeEventListener('pointerdown', down);
        window.removeEventListener('pointermove', move);
        window.removeEventListener('pointerup', up);
        window.removeEventListener('pointercancel', cancel);
        window.removeEventListener('lostpointercapture', cancel);
        window.removeEventListener('blur', cancel);
        window.removeEventListener('keydown', key);
      },
    });
  },

  dispose(id) {
    this.registrations.get(id)?.dispose();
    this.registrations.delete(id);
  },
};
