FROM gcc@sha256:27be068a2580bed7c32ccf2349352e29f86e0a5c8cda8c9c98c6b59e4ad98d53

WORKDIR /ResetAffinity
COPY workflow.sh .
COPY ResetAffinityLinux.c .

CMD ["./workflow.sh"]
