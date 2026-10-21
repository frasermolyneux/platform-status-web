FROM postgres:18-bookworm AS postgres

FROM scratch
COPY --from=postgres / /
ENV PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
    LANG=en_US.utf8 PGDATA=/var/lib/postgresql/data
COPY --chmod=0555 deployment/postgres-entrypoint.sh /usr/local/bin/status-postgres
USER 999:999
EXPOSE 5432
ENTRYPOINT ["/usr/local/bin/status-postgres"]
